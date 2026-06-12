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

                    match jobs.TryGet jobId with
                    | Some r ->
                        ignore (
                            task {
                                do! jobs.EmitErrorAsync(r, payload)
                                do! credentialRegistry.RevokeJobAsync(jobId, CancellationToken.None)
                            }
                        )
                    | None -> ()
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
                    | Some r ->
                        ignore (
                            task {
                                do! jobs.EmitErrorAsync(r, payload)
                                do! credentialRegistry.RevokeJobAsync(jobId, CancellationToken.None)
                            }
                        )
                    | None -> ()
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
            // Idempotency-key short-circuit.
            match submit.IdempotencyKey with
            | Some key when (jobs.LookupIdempotencyKey key).IsSome ->
                let existing = (jobs.LookupIdempotencyKey key).Value
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
                match inventory.Resolve submit.Agent with
                | Error err -> do! EnvelopeOut.respondWithError ctx requestId err ct
                | Ok(name, version, _) ->
                    let resolvedAgent = AgentRef.format name (Some version)
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
                            let! issued = issueCredentialsAsync provisioner credentialRegistry record ct

                            match issued with
                            | Error err ->
                                // Acceptance failed after registration —
                                // unwind state so the failed job does not
                                // surface in list/get and the idempotency
                                // key is free for a retry.
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

                                do! EnvelopeOut.respondWithError ctx requestId err ct
                            | Ok credentials ->
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
                                        Credentials = if List.isEmpty credentials then None else Some credentials
                                        AcceptedAt = record.CreatedAt
                                        TraceId = traceIdOpt
                                    }

                                // Capture for verbatim idempotent replay (§7.2).
                                record.AcceptedPayload <- Some accepted

                                do! sendAccepted ctx.Transport ctx.SessionId requestId jobId accepted ct

                                match agentHandlers.TryGetValue resolvedAgent with
                                | true, handler ->
                                    JobLauncher.launch jobs credentialRegistry timeProvider record handler
                                | _ ->
                                    do!
                                        EnvelopeOut.respondWithError
                                            ctx
                                            requestId
                                            (ARCPError.AgentNotAvailable resolvedAgent)
                                            ct
        }
        :> Task
