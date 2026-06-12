namespace ARCP.Runtime

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime.Auth
open ARCP.Runtime.Internal
open ARCP.Runtime.Store

/// Runtime configuration for `ArcpServer`.
type ArcpServerOptions =
    {
        Runtime: RuntimeIdentity
        Features: Set<string>
        HeartbeatIntervalSec: int
        ResumeWindowSec: int
        BearerVerifier: IBearerVerifier
        /// When true, clients may handshake with `auth.scheme = "none"`
        /// and receive an `AnonymousPrincipal`. Defaults to false so
        /// that configuring a bearer verifier is not silently bypassed.
        AllowAnonymousAuth: bool
        TimeProvider: TimeProvider
        Provisioner: ICredentialProvisioner option
        CredentialStore: ICredentialStore option
    }

[<RequireQualifiedAccess>]
module ArcpServerOptions =
    /// Sensible defaults: dev-mode bearer auth, every feature flag,
    /// 30s heartbeat, 600s resume window. Anonymous auth is off by
    /// default — opt in via `AllowAnonymousAuth = true` for local
    /// stdio/dev setups.
    let defaults: ArcpServerOptions =
        {
            Runtime =
                {
                    Name = "arcp-fsharp-runtime"
                    Version = Version.Sdk
                }
            Features = Features.All
            HeartbeatIntervalSec = 30
            ResumeWindowSec = 600
            BearerVerifier = DevModeBearerVerifier() :> IBearerVerifier
            AllowAnonymousAuth = false
            TimeProvider = TimeProvider.System
            Provisioner = None
            CredentialStore = None
        }

/// ARCP runtime / server entry point.
///
/// `RegisterAgent` (or `RegisterAgentVersion` + `SetDefaultAgentVersion`)
/// installs agent handlers. `HandleSessionAsync` runs one accepted
/// transport — one per WebSocket connection or stdio child.
type ArcpServer(options: ArcpServerOptions) =
    do
        match options.Provisioner, options.CredentialStore with
        | Some _, None ->
            invalidArg
                "options"
                "Provisioned credentials require an explicit CredentialStore for revocation reliability."
        | _ -> ()

        // §14: credentials MUST only be issued over authenticated
        // transports. Allowing anonymous sessions alongside a
        // provisioner would leak minted credentials to unauthenticated
        // peers, so the combination is rejected at startup.
        if options.AllowAnonymousAuth && options.Provisioner.IsSome then
            invalidArg
                "options"
                "AllowAnonymousAuth cannot be combined with a credential Provisioner (§14): credentials must only be issued over authenticated sessions."

    let inventory = AgentInventoryStore()

    let provisioner =
        options.Provisioner
        |> Option.defaultWith (fun () -> NoOpCredentialProvisioner() :> ICredentialProvisioner)

    let credentialStore =
        options.CredentialStore
        |> Option.defaultWith (fun () -> InMemoryCredentialStore() :> ICredentialStore)

    let credentialRegistry = CredentialRegistry(provisioner, credentialStore)

    let supportedFeatures =
        if options.Provisioner.IsSome then
            options.Features
        else
            options.Features
            |> Set.remove Features.ProvisionedCredentials
            |> Set.remove Features.ModelUse

    let eventLog =
        EventLog(
            { EventLogOptions.defaults with
                ResumeWindowSec = options.ResumeWindowSec
                TimeProvider = options.TimeProvider
            }
        )

    let sessions = ConcurrentDictionary<string, ServerSessionContext>()

    // Sessions whose transport dropped but whose buffered events are
    // still within the resume window (spec §6.3). A `session.resume`
    // reattaches one of these; pruning removes them with the window.
    let resumable = ConcurrentDictionary<string, ServerSessionContext>()
    let agentHandlers = ConcurrentDictionary<string, ArcpAgentHandler>()

    // Highest acked seq for a live session; a gone session can no
    // longer ack, so its buffered events age out by the window alone.
    let lastAckedFor (sid: string) : int64 =
        match sessions.TryGetValue sid with
        | true, ctx -> ctx.LastAckedSeq
        | _ -> Int64.MaxValue

    let isSessionActive (sid: string) : bool = sessions.ContainsKey sid

    // `JobManager` and the real outbox are mutually dependent: the
    // outbox needs `jobs` (for Subscribers / Terminate) and `jobs`
    // needs the outbox. We break the cycle with a ref cell that
    // `BuildOutbox` assigns into; mutation is unavoidable here.
    let outbox: IJobOutbox ref = ref Unchecked.defaultof<IJobOutbox>

    let jobs =
        JobManager(
            options.TimeProvider,
            { new IJobOutbox with
                member _.EmitJobEventAsync(rec0, body) = (!outbox).EmitJobEventAsync(rec0, body)
                member _.EmitJobResultAsync(rec0, p) = (!outbox).EmitJobResultAsync(rec0, p)
                member _.EmitJobErrorAsync(rec0, p) = (!outbox).EmitJobErrorAsync(rec0, p)

                member _.EmitCredentialRotatedAsync(rec0, cid, v) =
                    (!outbox).EmitCredentialRotatedAsync(rec0, cid, v)
            }
        )

    // Periodic pruning (spec §6.3 resume window): evict aged/acked
    // buffered events, release the buffers of gone sessions, and evict
    // terminal job records past the retention window.
    let pruneIntervalSec = max 1 (options.ResumeWindowSec / 4)

    let prune () =
        try
            eventLog.EvictExpired(lastAckedFor) |> ignore
            eventLog.PruneEmpty isSessionActive

            let cutoff =
                options.TimeProvider.GetUtcNow().AddSeconds(-float options.ResumeWindowSec)

            jobs.EvictTerminated cutoff |> ignore

            for kv in resumable do
                if kv.Value.LastInboundAt < cutoff then
                    resumable.TryRemove kv.Key |> ignore
        with _ ->
            ()

    let pruneTimer =
        options.TimeProvider.CreateTimer(
            TimerCallback(fun _ -> prune ()),
            null,
            TimeSpan.FromSeconds(float pruneIntervalSec),
            TimeSpan.FromSeconds(float pruneIntervalSec)
        )

    let registerHandler (name: string) (version: string) (h: ArcpAgentHandler) =
        agentHandlers.[name + "@" + version] <- h
        // The inventory stores an `AgentHandler` purely as a presence
        // marker — `JobSubmitFlow` discards it and dispatches via
        // `agentHandlers` keyed by `name@version`. The placeholder
        // raises so any regression that routes through it surfaces
        // loudly instead of returning a garbage JsonElement.
        let placeholder: AgentHandler =
            fun _ ->
                raise (
                    InvalidOperationException(
                        sprintf "ARCP runtime invariant: inventory placeholder invoked for %s@%s" name version
                    )
                )

        inventory.Register(name, version, placeholder)

    /// Register an agent under the default version (`default`).
    member _.RegisterAgent(name: string, handler: ArcpAgentHandler) : unit = registerHandler name "default" handler

    /// Register a specific version of an agent (spec §7.5).
    member _.RegisterAgentVersion(name: string, version: string, handler: ArcpAgentHandler) : unit =
        registerHandler name version handler

    /// Pin the default version returned for bare `name` requests.
    member _.SetDefaultAgentVersion(name: string, version: string) : unit = inventory.SetDefault(name, version)

    member internal _.AgentInventoryStore = inventory
    member internal _.EventLog = eventLog
    member internal _.Jobs = jobs

    member private _.BuildOutbox() : IJobOutbox =
        { new IJobOutbox with
            member _.EmitJobEventAsync(record, body) =
                task {
                    // §7.3/§9.5: no events after a terminal message.
                    if not record.TerminalEmitted then
                        do! EnvelopeOut.pushJobEvent sessions options.TimeProvider record.SessionId record.JobId body

                        for sid in jobs.Subscriptions.Subscribers record.JobId do
                            do! EnvelopeOut.pushJobEvent sessions options.TimeProvider sid record.JobId body

                        record.LastEventSeq <- record.LastEventSeq + 1L
                }
                :> Task

            member _.EmitJobResultAsync(record, payload) =
                task {
                    // Exactly one terminal message wins (§7.3, §9.5).
                    if jobs.TryClaimTerminal record then
                        do! EnvelopeOut.pushJobResult sessions record.SessionId record.JobId payload

                        for sid in jobs.Subscriptions.Subscribers record.JobId do
                            do! EnvelopeOut.pushJobResult sessions sid record.JobId payload

                        jobs.Terminate(record.JobId, payload.FinalStatus)
                }
                :> Task

            member _.EmitJobErrorAsync(record, payload) =
                task {
                    if jobs.TryClaimTerminal record then
                        do! EnvelopeOut.pushJobError sessions record.SessionId record.JobId payload

                        for sid in jobs.Subscriptions.Subscribers record.JobId do
                            do! EnvelopeOut.pushJobError sessions sid record.JobId payload

                        jobs.Terminate(record.JobId, payload.FinalStatus)
                }
                :> Task

            member _.EmitCredentialRotatedAsync(record, credentialId, newValue) =
                task {
                    // §14/§9.8.2: the submitting session gets the new
                    // value; subscribers get a redacted body (id only).
                    let ownerBody =
                        JobEventBody.Status(
                            StatusPhases.CredentialRotated,
                            Some(
                                Json.serialize
                                    {|
                                        id = credentialId
                                        value = newValue
                                    |}
                            )
                        )

                    let redactedBody =
                        JobEventBody.Status(StatusPhases.CredentialRotated, Some(Json.serialize {| id = credentialId |}))

                    do! EnvelopeOut.pushJobEvent sessions options.TimeProvider record.SessionId record.JobId ownerBody

                    for sid in jobs.Subscriptions.Subscribers record.JobId do
                        do! EnvelopeOut.pushJobEvent sessions options.TimeProvider sid record.JobId redactedBody

                    record.LastEventSeq <- record.LastEventSeq + 1L
                }
                :> Task
        }

    member private this.DispatchMessage
        (transport: ITransport)
        (ctxRef: ServerSessionContext option ref)
        (env: Envelope)
        (msg: Message)
        (ct: CancellationToken)
        : Task<bool> =
        task {
            match msg, ctxRef.Value with
            | Message.SessionHello hello, _ ->
                let! ctxOpt =
                    SessionHandshake.handleAsync
                        transport
                        options.Runtime
                        options.BearerVerifier
                        options.AllowAnonymousAuth
                        options.TimeProvider
                        eventLog
                        supportedFeatures
                        options.HeartbeatIntervalSec
                        options.ResumeWindowSec
                        inventory
                        env.Id
                        hello
                        ct

                match ctxOpt with
                | Some ctx ->
                    ctxRef.Value <- Some ctx
                    sessions.[ctx.SessionId.Value] <- ctx
                    return true
                | None -> return false
            | Message.SessionResume resume, _ -> return! this.HandleSessionResumeAsync transport ctxRef env.Id resume ct
            | _, None -> return true
            | Message.SessionClose _, Some ctx ->
                let envOut =
                    Message.SessionClosed { Reason = None }
                    |> Codec.toEnvelope
                    |> Envelope.withSessionId ctx.SessionId

                do! transport.SendAsync(envOut, ct)
                return false
            | Message.SessionPing p, Some ctx ->
                let pong: SessionPongPayload =
                    {
                        PingNonce = p.Nonce
                        ReceivedAt = options.TimeProvider.GetUtcNow()
                    }

                let envOut =
                    Message.SessionPong pong
                    |> Codec.toEnvelope
                    |> Envelope.withSessionId ctx.SessionId

                do! transport.SendAsync(envOut, ct)
                return true
            | Message.SessionAck a, Some ctx ->
                ctx.LastAckedSeq <- a.LastProcessedSeq
                return true
            | Message.SessionListJobs req, Some ctx ->
                do! this.HandleListJobsAsync env.Id ctx req ct
                return true
            | Message.JobSubmit submit, Some ctx ->
                do!
                    JobSubmitFlow.handleAsync
                        options.TimeProvider
                        inventory
                        jobs
                        provisioner
                        credentialRegistry
                        agentHandlers
                        ctx
                        env.Id
                        submit
                        env.TraceId
                        ct

                return true
            | Message.JobCancel c, Some ctx ->
                do! this.HandleJobCancelAsync env.Id ctx c ct
                return true
            | Message.JobSubscribe s, Some ctx ->
                do! this.HandleJobSubscribeAsync env.Id ctx s ct
                return true
            | Message.JobUnsubscribe u, Some ctx ->
                jobs.Subscriptions.Unsubscribe(JobId.ofString u.JobId, ctx.SessionId) |> ignore
                return true
            | _ -> return true
        }

    /// Run a single session over `transport`. Returns when the
    /// session ends (graceful close, transport drop, or `ct` fires).
    member this.HandleSessionAsync(transport: ITransport, ct: CancellationToken) : Task =
        task {
            outbox.Value <- this.BuildOutbox()
            let enumerable = transport.Receive(ct)
            let enumerator = enumerable.GetAsyncEnumerator(ct)
            let ctxRef: ServerSessionContext option ref = ref None

            try
                let mutable more = true

                while more do
                    let! has = enumerator.MoveNextAsync().AsTask()

                    if not has then
                        more <- false
                    else
                        let env = enumerator.Current

                        match Codec.toMessage env with
                        | Error err ->
                            // §12: malformed payloads / unknown types get a
                            // correlated INVALID_REQUEST; the session survives.
                            let payload: SessionErrorPayload =
                                {
                                    Code = ARCPError.code err
                                    Message = ARCPError.message err
                                    Retryable = ARCPError.retryable err
                                    Details = ARCPError.details err
                                }

                            let envOut =
                                Message.SessionError payload |> Codec.toEnvelope |> Envelope.withId env.Id

                            let envOut =
                                match ctxRef.Value with
                                | Some ctx -> Envelope.withSessionId ctx.SessionId envOut
                                | None -> envOut

                            do! transport.SendAsync(envOut, ct)
                        | Ok msg ->
                            let! keepGoing = this.DispatchMessage transport ctxRef env msg ct

                            if not keepGoing then
                                more <- false
            with :? OperationCanceledException ->
                ()

            do! enumerator.DisposeAsync().AsTask()

            match ctxRef.Value with
            | Some ctx ->
                jobs.Subscriptions.UnsubscribeAll ctx.SessionId
                sessions.TryRemove ctx.SessionId.Value |> ignore
                // Retain as resumable within the window (spec §6.3, §6.7):
                // in-flight jobs keep running and the client may reattach.
                ctx.LastInboundAt <- options.TimeProvider.GetUtcNow()
                resumable.[ctx.SessionId.Value] <- ctx
            | None -> ()
        }
        :> Task

    member private this.HandleListJobsAsync
        (requestId: string)
        (ctx: ServerSessionContext)
        (req: SessionListJobsPayload)
        (ct: CancellationToken)
        : Task =
        task {
            if not (ctx.NegotiatedFeatures.Contains Features.ListJobs) then
                do!
                    EnvelopeOut.respondWithError
                        ctx
                        requestId
                        (ARCPError.InvalidRequest("list_jobs feature not negotiated", None))
                        ct
            else
                // §6.6: bare-name agent filter matches any version;
                // `name@version` matches that version exactly.
                let agentMatches (filterAgent: string) (jobAgent: string) =
                    jobAgent = filterAgent || jobAgent.StartsWith(filterAgent + "@")

                // Stable ordering by JobId (ULIDs are time-ordered), so
                // repeated requests page deterministically (§6.6, §109).
                let ordered =
                    jobs.AllForPrincipal ctx.Principal.Id
                    |> Seq.filter (fun r ->
                        match req.Filter with
                        | None -> true
                        | Some f ->
                            (f.Status |> Option.map (List.contains r.Status) |> Option.defaultValue true)
                            && (f.Agent |> Option.map (fun a -> agentMatches a r.Agent) |> Option.defaultValue true)
                            && (f.CreatedAfter
                                |> Option.map (fun ca -> r.CreatedAt >= ca)
                                |> Option.defaultValue true))
                    |> Seq.sortBy (fun r -> r.JobId.Value)

                // Skip past the cursor (the last JobId of the prior page).
                let afterCursor =
                    match req.Cursor with
                    | Some c -> ordered |> Seq.filter (fun r -> r.JobId.Value > c)
                    | None -> ordered

                let limit =
                    match req.Limit with
                    | Some n when n > 0 -> n
                    | _ -> Int32.MaxValue

                // Take limit+1 to detect whether more pages remain without
                // materialising the entire visible set (§91).
                let takeCount = if limit = Int32.MaxValue then limit else limit + 1
                let page = afterCursor |> Seq.truncate takeCount |> Seq.toList
                let hasMore = List.length page > limit
                let pageRows = page |> List.truncate limit

                let nextCursor =
                    if hasMore then
                        pageRows |> List.tryLast |> Option.map (fun r -> r.JobId.Value)
                    else
                        None

                let resp: SessionJobsPayload =
                    {
                        RequestId = requestId
                        Jobs = pageRows |> List.map jobs.ToSummary
                        NextCursor = nextCursor
                    }

                let env =
                    Message.SessionJobs resp
                    |> Codec.toEnvelope
                    |> Envelope.withId requestId
                    |> Envelope.withSessionId ctx.SessionId

                do! ctx.Transport.SendAsync(env, ct)
        }
        :> Task

    member private _.HandleJobSubscribeAsync
        (requestId: string)
        (ctx: ServerSessionContext)
        (sub: JobSubscribePayload)
        (ct: CancellationToken)
        : Task =
        task {
            if not (ctx.NegotiatedFeatures.Contains Features.Subscribe) then
                do!
                    EnvelopeOut.respondWithError
                        ctx
                        requestId
                        (ARCPError.InvalidRequest("subscribe feature not negotiated", None))
                        ct
            else
                match jobs.TryGet(JobId.ofString sub.JobId) with
                | None -> do! EnvelopeOut.respondWithError ctx requestId (ARCPError.JobNotFound sub.JobId) ct
                | Some record when record.Principal.Id <> ctx.Principal.Id ->
                    do!
                        EnvelopeOut.respondWithError
                            ctx
                            requestId
                            (ARCPError.PermissionDenied("Subscribe denied", None))
                            ct
                | Some record ->
                    let wantHistory = sub.History |> Option.defaultValue false
                    let fromSeq = sub.FromEventSeq |> Option.defaultValue 0L

                    // §7.6: gather buffered `job.event`s for replay (from the
                    // owning session's log) before registering live delivery.
                    let replayResult =
                        if wantHistory then
                            eventLog.Replay(record.SessionId, fromSeq)
                            |> Result.map (fun entries ->
                                entries
                                |> Seq.filter (fun e ->
                                    e.Envelope.JobId = Some record.JobId.Value && e.Envelope.Type = "job.event")
                                |> Seq.toList)
                        else
                            Ok []

                    match replayResult with
                    | Error _ ->
                        // Buffer no longer covers from_event_seq.
                        do!
                            EnvelopeOut.respondWithError
                                ctx
                                requestId
                                (ARCPError.ResumeWindowExpired(fromSeq, options.ResumeWindowSec))
                                ct
                    | Ok replayEntries ->
                        jobs.Subscriptions.Subscribe(record.JobId, ctx.SessionId)

                        let payload: JobSubscribedPayload =
                            {
                                JobId = record.JobId.Value
                                CurrentStatus = record.Status
                                Agent = record.Agent
                                Lease = record.Lease
                                ParentJobId = record.ParentJobId
                                TraceId = record.TraceId
                                SubscribedFrom = fromSeq
                                Replayed = not (List.isEmpty replayEntries)
                            }

                        let env =
                            Message.JobSubscribed payload
                            |> Codec.toEnvelope
                            |> Envelope.withId requestId
                            |> Envelope.withSessionId ctx.SessionId
                            |> Envelope.withJobId record.JobId

                        do! ctx.Transport.SendAsync(env, ct)

                        // Replay buffered events into the subscriber's seq space
                        // before live events flow.
                        for e in replayEntries do
                            match Codec.toMessage e.Envelope with
                            | Ok(Message.JobEvent p) ->
                                do!
                                    EnvelopeOut.pushJobEvent
                                        sessions
                                        options.TimeProvider
                                        ctx.SessionId
                                        record.JobId
                                        p.Body
                            | _ -> ()
        }
        :> Task

    /// Handle `job.cancel` (spec §7.4). Acknowledge with `job.cancelled`
    /// then trigger cancellation; the terminal `job.error(CANCELLED)` is
    /// emitted by the launcher. Unknown ids return `JOB_NOT_FOUND`;
    /// requests from a non-owning session return `PERMISSION_DENIED`.
    member private _.HandleJobCancelAsync
        (requestId: string)
        (ctx: ServerSessionContext)
        (cancel: JobCancelPayload)
        (ct: CancellationToken)
        : Task =
        task {
            match jobs.TryGet(JobId.ofString cancel.JobId) with
            | None -> do! EnvelopeOut.respondWithError ctx requestId (ARCPError.JobNotFound cancel.JobId) ct
            | Some r when r.SessionId <> ctx.SessionId ->
                do!
                    EnvelopeOut.respondWithError
                        ctx
                        requestId
                        (ARCPError.PermissionDenied("Only the submitting session may cancel this job", None))
                        ct
            | Some r ->
                let ack =
                    Message.JobCancelled { JobId = cancel.JobId }
                    |> Codec.toEnvelope
                    |> Envelope.withId requestId
                    |> Envelope.withSessionId ctx.SessionId
                    |> Envelope.withJobId r.JobId

                do! ctx.Transport.SendAsync(ack, ct)

                try
                    r.Cancellation.Cancel()
                with _ ->
                    ()
        }
        :> Task

    /// Handle `session.resume` (spec §6.3). Validates the presented
    /// `(session_id, resume_token)` against a resumable session, replays
    /// buffered events with `seq > last_event_seq`, rotates the resume
    /// token, and resends a `session.welcome`. Returns `true` (and sets
    /// `ctxRef`) on success; `false` after a `RESUME_WINDOW_EXPIRED`.
    member private _.HandleSessionResumeAsync
        (transport: ITransport)
        (ctxRef: ServerSessionContext option ref)
        (requestId: string)
        (resume: SessionResumePayload)
        (ct: CancellationToken)
        : Task<bool> =
        task {
            let windowError = ARCPError.ResumeWindowExpired(resume.LastEventSeq, options.ResumeWindowSec)

            let writeError (err: ARCPError) : Task =
                let payload: SessionErrorPayload =
                    {
                        Code = ARCPError.code err
                        Message = ARCPError.message err
                        Retryable = ARCPError.retryable err
                        Details = ARCPError.details err
                    }

                let envOut =
                    Message.SessionError payload |> Codec.toEnvelope |> Envelope.withId requestId

                transport.SendAsync(envOut, ct)

            match resumable.TryGetValue resume.SessionId with
            | true, ctx when ctx.ResumeToken = resume.ResumeToken ->
                match eventLog.Replay(ctx.SessionId, resume.LastEventSeq) with
                | Error _ ->
                    do! writeError windowError
                    return false
                | Ok entries ->
                    let sid = ctx.SessionId
                    // Re-point the session at the new transport and rotate
                    // the resume token (it rotates on every welcome, §6.3).
                    ctx.Transport <- transport
                    ctx.ResumeToken <- (MessageId.newId ()).Value
                    ctx.LastInboundAt <- options.TimeProvider.GetUtcNow()
                    sessions.[sid.Value] <- ctx
                    resumable.TryRemove sid.Value |> ignore

                    let agents =
                        if ctx.NegotiatedFeatures.Contains Features.AgentVersions then
                            AgentInventory.Rich(inventory.ToRichInventory())
                        else
                            AgentInventory.Flat(inventory.ToFlatInventory())

                    let welcome: SessionWelcomePayload =
                        {
                            Runtime = options.Runtime
                            ResumeToken = ctx.ResumeToken
                            ResumeWindowSec = ctx.ResumeWindowSec
                            HeartbeatIntervalSec = ctx.HeartbeatIntervalSec
                            Capabilities =
                                {
                                    Encodings = [ "json" ]
                                    Features = ctx.NegotiatedFeatures
                                    Agents = agents
                                }
                        }

                    let welcomeEnv =
                        Message.SessionWelcome welcome
                        |> Codec.toEnvelope
                        |> Envelope.withSessionId sid
                        |> Envelope.withId requestId

                    do! transport.SendAsync(welcomeEnv, ct)

                    // Replay buffered events the client missed.
                    for entry in entries do
                        do! transport.SendAsync(entry.Envelope, ct)

                    ctxRef.Value <- Some ctx
                    return true
            | _ ->
                // Unknown session or token mismatch: the buffer no longer
                // covers the request.
                do! writeError windowError
                return false
        }

    interface IDisposable with
        member _.Dispose() =
            try
                pruneTimer.Dispose()
            with _ ->
                ()
