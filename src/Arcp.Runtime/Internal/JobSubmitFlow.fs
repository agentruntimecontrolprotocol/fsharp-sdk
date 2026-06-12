namespace ARCP.Runtime.Internal

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Runtime

/// Handle the `job.submit` request flow end-to-end:
/// idempotency replay → agent resolution → lease-constraint
/// validation → register + accept + launch.
[<RequireQualifiedAccess>]
module internal JobSubmitFlow =

    /// Stable fingerprint of the idempotency-relevant submission
    /// parameters (§7.2): agent, input, lease request/constraints, and
    /// max_runtime_sec. Used to detect conflicting key reuse.
    let private fingerprint (submit: JobSubmitPayload) : string =
        let canonical =
            Json.serialize
                {|
                    agent = submit.Agent
                    input = submit.Input
                    lease_request = submit.LeaseRequest
                    lease_constraints = submit.LeaseConstraints
                    max_runtime_sec = submit.MaxRuntimeSec
                |}

        use sha = System.Security.Cryptography.SHA256.Create()

        canonical
        |> System.Text.Encoding.UTF8.GetBytes
        |> sha.ComputeHash
        |> Convert.ToHexString

    /// Replay the exact original `job.accepted` payload (§7.2). The
    /// payload was captured at first acceptance, so the budget reflects
    /// the initially granted counters and credentials are preserved.
    let private replayAccepted (jobs: JobManager) (existing: string) : Result<JobAcceptedPayload, ARCPError> =
        match jobs.TryGet(JobId.ofString existing) with
        | Some r ->
            match r.AcceptedPayload with
            | Some accepted -> Ok accepted
            | None -> Error(ARCPError.JobNotFound existing)
        | None -> Error(ARCPError.JobNotFound existing)

    let private validateConstraints
        (timeProvider: TimeProvider)
        (raw: LeaseConstraints option)
        : Result<LeaseConstraints option, ARCPError> =
        match raw with
        | None -> Ok None
        | Some c when c.ExpiresAt.Offset <> TimeSpan.Zero ->
            Error(ARCPError.InvalidRequest("lease_constraints.expires_at must be UTC", None))
        | Some c when c.ExpiresAt <= timeProvider.GetUtcNow() ->
            Error(ARCPError.InvalidRequest("lease_constraints.expires_at must be in the future", None))
        | Some c -> Ok(Some c)

    let private buildWatchdog
        (timeProvider: TimeProvider)
        (jobs: JobManager)
        (credentialRegistry: CredentialRegistry)
        (jobId: JobId)
        (constraints: LeaseConstraints option)
        : ExpiryWatchdog option =
        constraints
        |> Option.map (fun c ->
            let w = new ExpiryWatchdog(timeProvider)

            w.Start(
                c.ExpiresAt,
                fun () ->
                    let payload: JobErrorPayload =
                        {
                            FinalStatus = JobStatus.Error
                            Code = "LEASE_EXPIRED"
                            Message = sprintf "Lease expired at %O" c.ExpiresAt
                            Retryable = false
                            Details = None
                        }

                    // §89: skip already-terminal jobs (the terminal gate also
                    // drops the emit, but avoid the work and revoke entirely).
                    match jobs.TryGet jobId with
                    | Some r when not r.TerminalEmitted ->
                        Task.Run(fun () ->
                            task {
                                try
                                    do! jobs.EmitErrorAsync(r, payload)
                                    do! credentialRegistry.RevokeJobAsync(jobId, CancellationToken.None)
                                with ex ->
                                    eprintfn "[ARCP] lease-expiry watchdog failed for job %s: %O" jobId.Value ex
                            }
                            :> Task)
                        |> ignore
                    | _ -> ()
            )

            w)

    /// Watchdog enforcing `max_runtime_sec` (§7.1). On expiry emits
    /// `job.error` with code `TIMEOUT` and `final_status: "timed_out"`,
    /// then revokes credentials. Guarded by the terminal gate so it
    /// never double-terminates.
    let private buildRuntimeWatchdog
        (timeProvider: TimeProvider)
        (jobs: JobManager)
        (credentialRegistry: CredentialRegistry)
        (jobId: JobId)
        (maxRuntimeSec: int option)
        : ExpiryWatchdog option =
        maxRuntimeSec
        |> Option.filter (fun n -> n > 0)
        |> Option.map (fun n ->
            let w = new ExpiryWatchdog(timeProvider)
            let deadline = timeProvider.GetUtcNow().AddSeconds(float n)

            w.Start(
                deadline,
                fun () ->
                    let payload: JobErrorPayload =
                        {
                            FinalStatus = JobStatus.TimedOut
                            Code = "TIMEOUT"
                            Message = sprintf "Job exceeded max_runtime_sec=%d" n
                            Retryable = true
                            Details = None
                        }

                    match jobs.TryGet jobId with
                    | Some r when not r.TerminalEmitted ->
                        Task.Run(fun () ->
                            task {
                                try
                                    do! jobs.EmitErrorAsync(r, payload)
                                    do! credentialRegistry.RevokeJobAsync(jobId, CancellationToken.None)
                                with ex ->
                                    eprintfn "[ARCP] runtime watchdog failed for job %s: %O" jobId.Value ex
                            }
                            :> Task)
                        |> ignore
                    | _ -> ()
            )

            w)

    let private issueCredentialsAsync
        (provisioner: ICredentialProvisioner)
        (registry: CredentialRegistry)
        (record: JobRecord)
        (ct: CancellationToken)
        : Task<Result<Credential list, ARCPError>> =
        task {
            // §14: never mint credentials for an anonymous principal,
            // even if a provisioner is configured.
            match record.Principal with
            | :? ARCP.Runtime.Auth.AnonymousPrincipal -> return Ok []
            | _ ->
                let ctx: CredentialIssueContext =
                    {
                        JobId = record.JobId
                        Principal = record.Principal
                        Lease = record.Lease
                        LeaseConstraints = record.Constraints
                        ParentJobId = record.ParentJobId |> Option.map JobId.ofString
                    }

                try
                    let! credentials = provisioner.IssueAsync(ctx, ct)

                    for cred in credentials do
                        do! registry.Track(record.JobId, cred)

                    return Ok credentials
                with
                | :? ArcpException as ax -> return Error ax.Error
                | :? UnauthorizedAccessException as ex -> return Error(ARCPError.PermissionDenied(ex.Message, None))
                | ex -> return Error(ARCPError.InternalError ex.Message)
        }

    let private sendAccepted
        (transport: ARCP.Client.ITransport)
        (sid: SessionId)
        (requestId: string)
        (jobId: JobId)
        (accepted: JobAcceptedPayload)
        (ct: CancellationToken)
        : Task =
        let env =
            Message.JobAccepted accepted
            |> Codec.toEnvelope
            |> Envelope.withId requestId
            |> Envelope.withSessionId sid
            |> Envelope.withJobId jobId

        transport.SendAsync(env, ct)

    /// Entry point for a `job.submit` envelope.
    let handleAsync
        (timeProvider: TimeProvider)
        (inventory: AgentInventoryStore)
        (jobs: JobManager)
        (provisioner: ICredentialProvisioner)
        (credentialRegistry: CredentialRegistry)
        (agentHandlers: ConcurrentDictionary<string, ArcpAgentHandler>)
        (ctx: ServerSessionContext)
        (requestId: string)
        (submit: JobSubmitPayload)
        (traceIdOpt: string option)
        (ct: CancellationToken)
        : Task =
        task {
            // Idempotency-key short-circuit. Single lookup (#52).
            let existingForKey = submit.IdempotencyKey |> Option.bind jobs.LookupIdempotencyKey

            match submit.IdempotencyKey, existingForKey with
            | Some key, Some existing ->
                let newFingerprint = fingerprint submit

                // §7.2: identical params → replay original job.accepted;
                // conflicting params under the same key → DUPLICATE_KEY.
                let conflicting =
                    match jobs.TryGet(JobId.ofString existing) with
                    | Some r ->
                        match r.IdempotencyFingerprint with
                        | Some fp -> fp <> newFingerprint
                        | None -> false
                    | None -> false

                if conflicting then
                    do! EnvelopeOut.respondWithError ctx requestId (ARCPError.DuplicateKey key) ct
                else
                    match replayAccepted jobs existing with
                    | Ok accepted ->
                        do! sendAccepted ctx.Transport ctx.SessionId requestId (JobId.ofString existing) accepted ct
                    | Error err -> do! EnvelopeOut.respondWithError ctx requestId err ct
            | _ ->
                let agentVersionsOn = ctx.NegotiatedFeatures.Contains Features.AgentVersions

                if not agentVersionsOn && submit.Agent.Contains '@' then
                    // §6.2/§7.5: cannot pin a version without negotiating
                    // agent_versions.
                    do!
                        EnvelopeOut.respondWithError
                            ctx
                            requestId
                            (ARCPError.InvalidRequest("agent_versions not negotiated; bare agent name required", None))
                            ct
                elif
                    submit.LeaseConstraints.IsSome
                    && not (ctx.NegotiatedFeatures.Contains Features.LeaseExpiresAt)
                then
                    // §6.2/§114: cannot use lease_constraints.expires_at
                    // without negotiating lease_expires_at.
                    do!
                        EnvelopeOut.respondWithError
                            ctx
                            requestId
                            (ARCPError.InvalidRequest("lease_expires_at not negotiated", None))
                            ct
                else

                    match inventory.Resolve submit.Agent with
                    | Error err -> do! EnvelopeOut.respondWithError ctx requestId err ct
                    | Ok(name, version, _) ->
                        // The handler is always keyed name@version; the agent
                        // surfaced on the wire omits the version when the feature
                        // wasn't negotiated (§6.2).
                        let handlerKey = AgentRef.format name (Some version)

                        let resolvedAgent = if agentVersionsOn then handlerKey else name

                        let lease = submit.LeaseRequest |> Option.defaultValue Lease.empty

                        match validateConstraints timeProvider submit.LeaseConstraints with
                        | Error err -> do! EnvelopeOut.respondWithError ctx requestId err ct
                        | Ok constraints ->
                            let jobId = JobId.newId ()

                            // Claim the idempotency key first so a duplicate
                            // submission short-circuits before any side effects
                            // (record registration, watchdog start, provisioner
                            // call). Without this, two concurrent submits with
                            // the same key both fell through and created jobs.
                            let claimResult =
                                match submit.IdempotencyKey with
                                | Some key -> jobs.TryClaimIdempotencyKey(key, jobId)
                                | None -> Ok()

                            match claimResult with
                            | Error err -> do! EnvelopeOut.respondWithError ctx requestId err ct
                            | Ok() ->
                                let budgets = BudgetCounters()

                                // §114: only track budget counters when cost.budget
                                // was negotiated.
                                if ctx.NegotiatedFeatures.Contains Features.CostBudget then
                                    budgets.SetInitial(Lease.initialBudgets lease)

                                let cts = new CancellationTokenSource()
                                let watchdog = buildWatchdog timeProvider jobs credentialRegistry jobId constraints

                                let runtimeWatchdog =
                                    buildRuntimeWatchdog timeProvider jobs credentialRegistry jobId submit.MaxRuntimeSec

                                let record: JobRecord =
                                    {
                                        JobId = jobId
                                        SessionId = ctx.SessionId
                                        Principal = ctx.Principal
                                        Agent = resolvedAgent
                                        Input = submit.Input
                                        Lease = lease
                                        Constraints = constraints
                                        Credentials = []
                                        Budgets = budgets
                                        ParentJobId = None
                                        TraceId = traceIdOpt
                                        CreatedAt = timeProvider.GetUtcNow()
                                        Cancellation = cts
                                        Watchdog = watchdog
                                        RuntimeWatchdog = runtimeWatchdog
                                        TerminalEmitted = false
                                        AcceptedPayload = None
                                        Status = JobStatus.Pending
                                        LastEventSeq = 0L
                                        StreamResultId = None
                                        IdempotencyFingerprint =
                                            submit.IdempotencyKey |> Option.map (fun _ -> fingerprint submit)
                                        IdempotencyKey = submit.IdempotencyKey
                                        TerminatedAt = None
                                    }

                                jobs.Register record

                                // Unwind all acceptance side effects so a failed
                                // acceptance leaves no record, frees the idempotency
                                // key, stops timers, and revokes any credentials.
                                let unwind () : Task =
                                    task {
                                        jobs.Unregister jobId

                                        match submit.IdempotencyKey with
                                        | Some key -> jobs.ReleaseIdempotencyKey(key, jobId)
                                        | None -> ()

                                        watchdog |> Option.iter (fun w -> (w :> IDisposable).Dispose())
                                        runtimeWatchdog |> Option.iter (fun w -> (w :> IDisposable).Dispose())

                                        try
                                            cts.Cancel()
                                        with _ ->
                                            ()

                                        try
                                            cts.Dispose()
                                        with _ ->
                                            ()

                                        try
                                            do! credentialRegistry.RevokeJobAsync(jobId, ct)
                                        with _ ->
                                            ()
                                    }
                                    :> Task

                                // §114: only issue provisioned credentials when the
                                // feature was negotiated.
                                let! issued =
                                    if ctx.NegotiatedFeatures.Contains Features.ProvisionedCredentials then
                                        issueCredentialsAsync provisioner credentialRegistry record ct
                                    else
                                        Task.FromResult(Ok [])

                                match issued with
                                | Error err ->
                                    do! unwind ()
                                    do! EnvelopeOut.respondWithError ctx requestId err ct
                                | Ok credentials ->
                                    // §47: resolve the handler BEFORE accepting so a
                                    // missing handler does not produce both a
                                    // job.accepted and an error, nor leak the record.
                                    match agentHandlers.TryGetValue handlerKey with
                                    | false, _ ->
                                        do! unwind ()

                                        do!
                                            EnvelopeOut.respondWithError
                                                ctx
                                                requestId
                                                (ARCPError.AgentNotAvailable resolvedAgent)
                                                ct
                                    | true, handler ->
                                        record.Credentials <- credentials

                                        let initialBudget =
                                            if budgets.Snapshot() = Map.empty then
                                                None
                                            else
                                                Some(budgets.Snapshot())

                                        let accepted: JobAcceptedPayload =
                                            {
                                                JobId = jobId.Value
                                                Lease = lease
                                                LeaseConstraints = constraints
                                                Budget = initialBudget
                                                Credentials =
                                                    if List.isEmpty credentials then None else Some credentials
                                                AcceptedAt = record.CreatedAt
                                                TraceId = traceIdOpt
                                            }

                                        // Capture for verbatim idempotent replay (§7.2).
                                        record.AcceptedPayload <- Some accepted

                                        do! sendAccepted ctx.Transport ctx.SessionId requestId jobId accepted ct
                                        JobLauncher.launch jobs credentialRegistry timeProvider record handler
        }
        :> Task
