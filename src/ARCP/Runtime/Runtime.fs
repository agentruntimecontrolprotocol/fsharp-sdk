namespace ARCP.Runtime

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Json.Schema
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Session
open ARCP.Messages.Control
open ARCP.Messages.Execution
open ARCP.Messages.Streaming
open ARCP.Messages.Human
open ARCP.Messages.Permissions
open ARCP.Messages.Registry
open ARCP.Messages.Subscriptions
open ARCP.Messages.Artifacts
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Store.EventLog
open ARCP.Runtime.Pending
open ARCP.Runtime.Session

/// <summary>
/// Context passed to every tool handler invocation (RFC §10.2, §14, §15).
/// Exposes the in-flight job/session ids plus first-class affordances for
/// human-in-the-loop, permission challenges, lease management, progress, and
/// streams.
/// </summary>
type ToolContext =
    {
        /// <summary>The job this handler is running under (RFC §10).</summary>
        JobId: JobId
        /// <summary>The session this job lives in (RFC §9).</summary>
        SessionId: SessionId
        /// <summary>
        /// Request a free-form value from the human (RFC §14.1). Honours
        /// <paramref name="expiresAt"/> via the runtime's
        /// <see cref="TimeProvider"/>.
        /// </summary>
        RequestHumanInputAsync:
            string * JsonElement option * JsonElement option * DateTimeOffset * CancellationToken
                -> Task<Result<JsonElement, ARCPError>>
        /// <summary>
        /// Pick one of a fixed set of options (RFC §14.2). Returns the chosen
        /// option's id, or <c>Error (DeadlineExceeded _)</c> when the prompt
        /// expires.
        /// </summary>
        RequestChoiceAsync:
            string * ChoiceOption list * DateTimeOffset * CancellationToken -> Task<Result<string, ARCPError>>
        /// <summary>
        /// Issue a <c>permission.request</c> and await the client's response
        /// (RFC §15.4). On grant the runtime allocates a lease through
        /// <see cref="LeaseManager"/> and returns it.
        /// </summary>
        RequestPermissionAsync:
            string * string * string * string option * int option * CancellationToken -> Task<Result<Lease, ARCPError>>
        /// <summary>Emit a <c>job.progress</c> envelope (RFC §10.3).</summary>
        ProgressAsync: int option * string option -> Task
        /// <summary>Open a new outgoing stream attached to this job (RFC §11).</summary>
        OpenStreamAsync: StreamKind * string option * string option -> Task<StreamWriter>
        /// <summary>The runtime-owned <see cref="LeaseManager"/>.</summary>
        LeaseManager: LeaseManager
        /// <summary>Cancellation token tied to the job's lifetime.</summary>
        CancellationToken: CancellationToken
    }

/// <summary>
/// Signature for an in-process tool handler. The handler receives a
/// <see cref="ToolContext"/> and the invocation arguments; the runtime
/// converts its result into <c>job.completed</c>/<c>job.failed</c>.
/// </summary>
type ToolHandler = ToolContext -> JsonElement -> Task<Result<JsonElement, ARCPError>>

/// <summary>Configuration for an ARCP <see cref="Runtime"/>.</summary>
type RuntimeOptions =
    {
        /// <summary>Runtime identity advertised in <c>session.accepted</c>.</summary>
        RuntimeIdentity: RuntimeIdentity
        /// <summary>Server-offered capability set; the negotiated set is the intersection with the client request.</summary>
        OfferedCapabilities: Capabilities
        /// <summary>Heartbeat cadence used by the <see cref="JobManager"/>.</summary>
        HeartbeatInterval: TimeSpan
        /// <summary>Consecutive missed heartbeats before a job is considered lost.</summary>
        MissedDeadlineLimit: int
        /// <summary>Stream channel capacity (per-stream).</summary>
        StreamCapacity: int
        /// <summary>Optional time provider; defaults to <see cref="TimeProvider.System"/>.</summary>
        TimeProvider: TimeProvider
        /// <summary>Optional logger factory.</summary>
        LoggerFactory: ILoggerFactory option
        /// <summary>Default lease lifetime when a grant omits one (seconds).</summary>
        DefaultLeaseSeconds: int
        /// <summary>Lease sweep interval; defaults to 5 seconds.</summary>
        LeaseSweepInterval: TimeSpan
        /// <summary>
        /// Event log used for subscription backfill (RFC §13.2) and resume
        /// (RFC §19). When <c>None</c>, the runtime creates a private
        /// in-memory log per session.
        /// </summary>
        EventLog: EventLog option
        /// <summary>Default artifact retention when a put omits one (RFC §16.2).</summary>
        ArtifactDefaultRetention: TimeSpan
        /// <summary>Maximum artifact retention (RFC §16.2).</summary>
        ArtifactMaxRetention: TimeSpan
        /// <summary>Artifact sweep cadence (RFC §16.2).</summary>
        ArtifactSweepInterval: TimeSpan
    }

[<RequireQualifiedAccess>]
module RuntimeOptions =
    /// <summary>Reasonable defaults: identity-only, no optional capabilities.</summary>
    let defaults: RuntimeOptions =
        {
            RuntimeIdentity =
                {
                    Kind = "arcp-fsharp"
                    Version = Version.Sdk
                    Fingerprint = None
                    TrustLevel = None
                }
            OfferedCapabilities = Capabilities.empty
            HeartbeatInterval = TimeSpan.FromSeconds 30.0
            MissedDeadlineLimit = 2
            StreamCapacity = 32
            TimeProvider = TimeProvider.System
            LoggerFactory = None
            DefaultLeaseSeconds = 300
            LeaseSweepInterval = TimeSpan.FromSeconds 5.0
            EventLog = None
            ArtifactDefaultRetention = TimeSpan.FromHours 1.0
            ArtifactMaxRetention = TimeSpan.FromHours 24.0
            ArtifactSweepInterval = TimeSpan.FromSeconds 60.0
        }

/// <summary>
/// ARCP runtime (RFC §9, §10, §11, §14, §15). Drives the session handshake
/// then dispatches subsequent envelopes to the job, stream, lease, and
/// human-in-the-loop subsystems.
/// </summary>
type Runtime(transport: ITransport, validator: IAuthValidator, logger: ILogger, options: RuntimeOptions) =

    let cts = new CancellationTokenSource()

    let tools = ConcurrentDictionary<string, ToolHandler>()

    let humanInputPending = PendingRegistry<HumanInputResponse>()
    let humanChoicePending = PendingRegistry<HumanChoiceResponse>()
    let permissionPending = PendingRegistry<Result<PermissionGrant, PermissionDenied>>()

    // Schemas registered alongside each pending human-input request. The
    // dispatch loop consults this map when validating responses, so a single
    // request id maps to its schema (RFC §14.1).
    let humanInputSchemas = ConcurrentDictionary<MessageId, JsonElement option>()

    let eventLog =
        match options.EventLog with
        | Some l -> l
        | None -> new EventLog(EventLogOptions.memory ())

    let mutable currentSession: SessionId option = None

    // The runtime's send chokepoint (RFC §13.1, §19). Every outbound envelope
    // routes through here so it can be (a) stamped with session id, (b)
    // appended to the event log idempotently, (c) sent on the transport, and
    // (d) fanned out to matching subscriptions.
    //
    // `subscriptions` is constructed below at the same time the runtime is
    // wired up; this field is the back-edge of that cycle and is bound exactly
    // once during init (see `do subscriptionManager <- Some subscriptions`).
    let mutable subscriptionManager: SubscriptionManager option = None

    // Tracked so DisposeAsync/StopAsync can cancel job heartbeats and stream
    // pumps that would otherwise outlive the test (RFC §10.3 heartbeat,
    // RFC §11 streams).
    let mutable activeJobManager: JobManager option = None
    let mutable activeStreamManager: StreamManager option = None

    /// Stamp `env` with the current session id (if any) and append it to the
    /// event log. Append is best-effort: cancellation propagates silently,
    /// other failures are logged but never bubble up — the send pipeline must
    /// keep flowing even when the log is unavailable (RFC §19).
    let stampAndLog (env: Envelope<MessageType>) : Task<Envelope<MessageType>> =
        task {
            let stamped =
                match env.SessionId, currentSession with
                | None, Some sid -> { env with SessionId = Some sid }
                | _ -> env

            try
                let! _ = eventLog.AppendAsync stamped
                ()
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.LogWarning(ex, "event log append failed for {MessageId}", stamped.Id)

            return stamped
        }

    let sendEnvelope (env: Envelope<MessageType>) : Task =
        task {
            let! stamped = stampAndLog env

            try
                do! transport.SendAsync(stamped, cts.Token)
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.LogWarning(ex, "transport send failed for {MessageId}", stamped.Id)

            match subscriptionManager with
            | Some sm ->
                try
                    do! sm.PublishAsync stamped
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.LogWarning(ex, "subscription fanout failed for {MessageId}", stamped.Id)
            | None -> ()
        }
        :> Task

    // For "replay" sends during resume: bypass log append (already logged)
    // and subscription fanout (the events were already published live).
    let replaySend (env: Envelope<MessageType>) : Task =
        task {
            try
                do! transport.SendAsync(env, cts.Token)
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.LogWarning(ex, "transport replay send failed for {MessageId}", env.Id)
        }
        :> Task

    let send (env: Envelope<MessageType>) (_ct: CancellationToken) = sendEnvelope env

    let sendIgnoring (env: Envelope<MessageType>) : Task = sendEnvelope env

    let leaseManager =
        new LeaseManager(options.TimeProvider, sendIgnoring, options.LeaseSweepInterval)

    let subscriptions = new SubscriptionManager(eventLog, sendIgnoring)
    do subscriptionManager <- Some subscriptions

    let artifactStore =
        new ArtifactStore(
            options.TimeProvider,
            options.ArtifactDefaultRetention,
            options.ArtifactMaxRetention,
            options.ArtifactSweepInterval
        )

    // Map session id to principal for subscription authorization (RFC §13.2).
    let sessionPrincipals = ConcurrentDictionary<SessionId, string>()

    let nack (correlationId: MessageId) (code: string) (message: string) : Envelope<MessageType> =
        Envelopes.nack
            {
                Code = code
                Message = message
                Details = None
            }
        |> Envelope.withCorrelation correlationId

    let rejectAndClose (code: string) (reason: string) (ct: CancellationToken) =
        task {
            logger.LogWarning("session rejected: {Code} {Reason}", code, reason)

            let env = Envelopes.sessionRejected { Code = code; Reason = Some reason }

            do! send env ct
            return Closed reason
        }

    let handleOpen (env: Envelope<MessageType>) (payload: SessionOpen) (ct: CancellationToken) =
        task {
            let schemeOpt =
                match payload.Auth.Scheme with
                | "bearer" -> payload.Auth.Token |> Option.map Bearer
                | "signed_jwt" -> payload.Auth.Token |> Option.map Jwt
                | "none" -> Some Anonymous
                | _ -> None

            match schemeOpt with
            | None ->
                let! st = rejectAndClose "UNAUTHENTICATED" (sprintf "unsupported scheme %s" payload.Auth.Scheme) ct
                return st
            | Some scheme ->
                let! result = validator.ValidateAsync(scheme, ct)

                match result with
                | Error err ->
                    let! st = rejectAndClose (ARCPError.code err) (ARCPError.message err) ct
                    return st
                | Ok authResult ->
                    if scheme = Anonymous && not options.OfferedCapabilities.Anonymous then
                        let! st = rejectAndClose "UNAUTHENTICATED" "anonymous sessions not permitted" ct
                        return st
                    else
                        match negotiate payload.Capabilities options.OfferedCapabilities with
                        | Error missing ->
                            let! st = rejectAndClose "UNIMPLEMENTED" (sprintf "unsupported capabilities: %s" missing) ct

                            return st
                        | Ok negotiated ->
                            let sid = SessionId.create ()

                            let acceptedEnv =
                                Envelopes.sessionAccepted
                                    {
                                        SessionId = sid
                                        Runtime = options.RuntimeIdentity
                                        Capabilities = negotiated
                                        Lease = None
                                    }
                                |> Envelope.withCorrelation env.Id
                                |> Envelope.withSession sid

                            do! send acceptedEnv ct
                            return Authenticated(authResult.Principal, sid, negotiated, None)
        }

    let mutable runtimePrincipal: string = "anonymous"

    /// Validate `value` against an optional JSON schema element. Returns Ok if no
    /// schema is provided, or if the schema validates the value.
    let validateAgainstSchema (schema: JsonElement option) (value: JsonElement) : Result<unit, string> =
        match schema with
        | None -> Ok()
        | Some s ->
            try
                let json = s.GetRawText()
                let parsed = JsonSchema.FromText(json)
                let result = parsed.Evaluate(value)

                if result.IsValid then
                    Ok()
                else
                    Error "value failed schema validation"
            with ex ->
                Error(sprintf "schema validation error: %s" ex.Message)

    let buildToolContext
        (jobId: JobId)
        (sessionId: SessionId)
        (jobManager: JobManager)
        (streamManager: StreamManager)
        (requestHumanInput:
            string
                -> JsonElement option
                -> JsonElement option
                -> DateTimeOffset
                -> CancellationToken
                -> Task<Result<JsonElement, ARCPError>>)
        (requestChoice:
            string -> ChoiceOption list -> DateTimeOffset -> CancellationToken -> Task<Result<string, ARCPError>>)
        (requestPermission:
            string
                -> string
                -> string
                -> string option
                -> int option
                -> CancellationToken
                -> Task<Result<Lease, ARCPError>>)
        (ct: CancellationToken)
        : ToolContext =
        {
            JobId = jobId
            SessionId = sessionId
            RequestHumanInputAsync =
                fun (prompt, schema, dflt, expiresAt, innerCt) -> requestHumanInput prompt schema dflt expiresAt innerCt
            RequestChoiceAsync =
                fun (prompt, options, expiresAt, innerCt) -> requestChoice prompt options expiresAt innerCt
            RequestPermissionAsync =
                fun (permission, resource, operation, reason, leaseSeconds, innerCt) ->
                    requestPermission permission resource operation reason leaseSeconds innerCt
            ProgressAsync = fun (percent, message) -> jobManager.ProgressAsync(jobId, percent, message) :> Task
            OpenStreamAsync =
                fun (kind, contentType, encoding) ->
                    streamManager.OpenWriterAsync(kind, Some jobId, ?contentType = contentType, ?encoding = encoding)
            LeaseManager = leaseManager
            CancellationToken = ct
        }

    /// Request human input from the client and await the response.
    let requestHumanInputCore
        (sessionId: SessionId)
        (jobId: JobId)
        (prompt: string)
        (responseSchema: JsonElement option)
        (defaultValue: JsonElement option)
        (expiresAt: DateTimeOffset)
        (ct: CancellationToken)
        : Task<Result<JsonElement, ARCPError>> =
        task {
            let payload: HumanInputRequest =
                {
                    Prompt = prompt
                    ResponseSchema = responseSchema
                    Default = defaultValue
                    ExpiresAt = expiresAt
                }

            let env =
                Envelopes.humanInputRequest payload
                |> Envelope.withSession sessionId
                |> Envelope.withJob jobId

            let requestId = env.Id
            humanInputSchemas.[requestId] <- responseSchema

            let now = options.TimeProvider.GetUtcNow()
            let remaining = expiresAt - now

            let registered = humanInputPending.RegisterAsync(requestId, None, ct)

            do! sendIgnoring env

            let expirationCts = new CancellationTokenSource()
            use _ = ct.Register(fun () -> expirationCts.Cancel())

            let delay =
                if remaining > TimeSpan.Zero then
                    Task.Delay(remaining, options.TimeProvider, expirationCts.Token)
                else
                    Task.Delay(TimeSpan.Zero, options.TimeProvider, expirationCts.Token)

            let! winner = Task.WhenAny(registered :> Task, delay)

            if winner = (registered :> Task) then
                expirationCts.Cancel()
                let! response = registered
                return Ok response.Value
            else
                humanInputPending.Cancel(requestId) |> ignore
                humanInputSchemas.TryRemove requestId |> ignore

                match defaultValue with
                | Some d ->
                    let syntheticResponse: HumanInputResponse =
                        {
                            Value = d
                            RespondedBy = Some "default"
                            RespondedAt = Some(options.TimeProvider.GetUtcNow())
                        }

                    let respEnv =
                        Envelopes.humanInputResponse syntheticResponse
                        |> Envelope.withSession sessionId
                        |> Envelope.withJob jobId
                        |> Envelope.withCorrelation requestId

                    do! sendIgnoring respEnv
                    return Ok d
                | None ->
                    let cancelledEnv =
                        Envelopes.humanInputCancelled
                            {
                                Code = "DEADLINE_EXCEEDED"
                                Reason = Some "human input expired"
                            }
                        |> Envelope.withSession sessionId
                        |> Envelope.withJob jobId
                        |> Envelope.withCorrelation requestId

                    do! sendIgnoring cancelledEnv
                    return Error(DeadlineExceeded "human input")
        }

    let requestChoiceCore
        (sessionId: SessionId)
        (jobId: JobId)
        (prompt: string)
        (choiceOptions: ChoiceOption list)
        (expiresAt: DateTimeOffset)
        (ct: CancellationToken)
        : Task<Result<string, ARCPError>> =
        task {
            let payload: HumanChoiceRequest =
                {
                    Prompt = prompt
                    Options = choiceOptions
                    ExpiresAt = expiresAt
                }

            let env =
                Envelopes.humanChoiceRequest payload
                |> Envelope.withSession sessionId
                |> Envelope.withJob jobId

            let requestId = env.Id

            let now = options.TimeProvider.GetUtcNow()
            let remaining = expiresAt - now

            let registered = humanChoicePending.RegisterAsync(requestId, None, ct)

            do! sendIgnoring env

            let expirationCts = new CancellationTokenSource()
            use _ = ct.Register(fun () -> expirationCts.Cancel())

            let delay =
                if remaining > TimeSpan.Zero then
                    Task.Delay(remaining, options.TimeProvider, expirationCts.Token)
                else
                    Task.Delay(TimeSpan.Zero, options.TimeProvider, expirationCts.Token)

            let! winner = Task.WhenAny(registered :> Task, delay)

            if winner = (registered :> Task) then
                expirationCts.Cancel()
                let! response = registered
                return Ok response.ChoiceId
            else
                humanChoicePending.Cancel(requestId) |> ignore

                let cancelledEnv =
                    Envelopes.humanInputCancelled
                        {
                            Code = "DEADLINE_EXCEEDED"
                            Reason = Some "human choice expired"
                        }
                    |> Envelope.withSession sessionId
                    |> Envelope.withJob jobId
                    |> Envelope.withCorrelation requestId

                do! sendIgnoring cancelledEnv
                return Error(DeadlineExceeded "human choice")
        }

    let requestPermissionCore
        (sessionId: SessionId)
        (jobId: JobId)
        (permission: string)
        (resource: string)
        (operation: string)
        (reason: string option)
        (leaseSeconds: int option)
        (ct: CancellationToken)
        : Task<Result<Lease, ARCPError>> =
        task {
            let payload: PermissionRequest =
                {
                    Permission = permission
                    Resource = resource
                    Operation = operation
                    Reason = reason
                    RequestedLeaseSeconds = leaseSeconds
                }

            let env =
                Envelopes.permissionRequest payload
                |> Envelope.withSession sessionId
                |> Envelope.withJob jobId

            let requestId = env.Id

            let registered = permissionPending.RegisterAsync(requestId, None, ct)

            do! sendIgnoring env

            try
                let! outcome = registered

                match outcome with
                | Ok grant ->
                    let seconds = grant.LeaseSeconds |> Option.defaultValue options.DefaultLeaseSeconds

                    let! lease =
                        leaseManager.GrantAsync(
                            permission,
                            resource,
                            operation,
                            runtimePrincipal,
                            seconds,
                            Some sessionId,
                            correlationId = requestId
                        )

                    return Ok lease
                | Error denied ->
                    let reasonText = denied.Reason |> Option.defaultValue "denied"

                    return
                        Error(
                            ARCPError.PermissionDenied(permission, sprintf "%s on %s: %s" operation resource reasonText)
                        )
            with :? OperationCanceledException ->
                return Error(Cancelled "permission request cancelled")
        }

    let handleAuthenticated
        (sessionId: SessionId)
        (jobManager: JobManager)
        (streamManager: StreamManager)
        (env: Envelope<MessageType>)
        (ct: CancellationToken)
        =
        task {
            match env.Payload with
            | Ping p ->
                let pong = Envelopes.pong { Nonce = p.Nonce } |> Envelope.withCorrelation env.Id
                do! send pong ct
                return ()
            | SessionClose _ ->
                logger.LogInformation("session closed by peer")
                return ()
            | ToolInvoke ti ->
                let handler =
                    match tools.TryGetValue ti.Tool with
                    | true, h -> h
                    | _ -> fun _ _ -> Task.FromResult(Error(Unimplemented(sprintf "tool %s not registered" ti.Tool)))

                let mutable assignedJobId: JobId = JobId.create ()

                let run (innerCt: CancellationToken) =
                    task {
                        let ctx =
                            buildToolContext
                                assignedJobId
                                sessionId
                                jobManager
                                streamManager
                                (requestHumanInputCore sessionId assignedJobId)
                                (requestChoiceCore sessionId assignedJobId)
                                (requestPermissionCore sessionId assignedJobId)
                                innerCt

                        let! r = handler ctx ti.Arguments
                        return r
                    }

                let! jid = jobManager.AcceptAsync(sessionId, ti.Tool, run, correlationId = env.Id)
                assignedJobId <- jid
                return ()
            | JobHeartbeat hb ->
                match env.JobId with
                | Some jid ->
                    do! jobManager.RecordHeartbeatAsync(jid, hb.Sequence, hb.DeadlineMs)
                    return ()
                | None -> return ()
            | Cancel c ->
                match c.Target with
                | "job" ->
                    let jid = JobId.ofString c.TargetId
                    let! _ = jobManager.CancelAsync(jid, c.Reason, c.DeadlineMs)
                    return ()
                | _ ->
                    let n = nack env.Id "UNIMPLEMENTED" (sprintf "cancel target %s" c.Target)
                    do! send n ct
                    return ()
            | Interrupt i ->
                match i.JobId with
                | Some jid ->
                    let! _ = jobManager.InterruptAsync(jid, i.Reason |> Option.defaultValue "interrupted")
                    return ()
                | None ->
                    let n = nack env.Id "INVALID_ARGUMENT" "interrupt requires job_id"
                    do! send n ct
                    return ()
            | HumanInputResponse response ->
                match env.CorrelationId with
                | Some corr ->
                    let schema =
                        match humanInputSchemas.TryGetValue corr with
                        | true, s -> s
                        | _ -> None

                    let validation =
                        match schema with
                        | None -> Ok()
                        | Some s ->
                            try
                                let json = s.GetRawText()
                                let parsed = JsonSchema.FromText(json)
                                let result = parsed.Evaluate(response.Value)

                                if result.IsValid then
                                    Ok()
                                else
                                    Error "response failed schema validation"
                            with ex ->
                                Error(sprintf "schema validation error: %s" ex.Message)

                    match validation with
                    | Ok() ->
                        humanInputSchemas.TryRemove corr |> ignore
                        humanInputPending.Resolve(corr, response) |> ignore
                    | Error msg ->
                        let n = nack env.Id "INVALID_ARGUMENT" msg
                        do! send n ct

                    return ()
                | None ->
                    let n =
                        nack env.Id "INVALID_ARGUMENT" "human.input.response requires correlation_id"

                    do! send n ct
                    return ()
            | HumanChoiceResponse response ->
                match env.CorrelationId with
                | Some corr ->
                    humanChoicePending.Resolve(corr, response) |> ignore
                    return ()
                | None ->
                    let n =
                        nack env.Id "INVALID_ARGUMENT" "human.choice.response requires correlation_id"

                    do! send n ct
                    return ()
            | PermissionGrant grant ->
                match env.CorrelationId with
                | Some corr ->
                    permissionPending.Resolve(corr, Ok grant) |> ignore
                    return ()
                | None ->
                    let n = nack env.Id "INVALID_ARGUMENT" "permission.grant requires correlation_id"
                    do! send n ct
                    return ()
            | PermissionDenied denied ->
                match env.CorrelationId with
                | Some corr ->
                    permissionPending.Resolve(corr, Error denied) |> ignore
                    return ()
                | None ->
                    let n = nack env.Id "INVALID_ARGUMENT" "permission.deny requires correlation_id"
                    do! send n ct
                    return ()
            | LeaseRefresh refresh ->
                let additional =
                    refresh.RequestedSeconds |> Option.defaultValue options.DefaultLeaseSeconds

                let! result = leaseManager.ExtendAsync(refresh.LeaseId, additional)

                match result with
                | Ok _ -> return ()
                | Error err ->
                    let n = nack env.Id (ARCPError.code err) (ARCPError.message err)
                    do! send n ct
                    return ()
            | StreamOpen _
            | StreamChunk _
            | StreamClose _
            | StreamError _ ->
                do! streamManager.HandleAsync env
                return ()
            | Backpressure _ ->
                match env.StreamId with
                | Some sid ->
                    let _ = sid
                    return ()
                | None -> return ()
            | Subscribe sub ->
                let principalOf (sid: SessionId) : string option =
                    match sessionPrincipals.TryGetValue sid with
                    | true, p -> Some p
                    | _ -> None

                let principal =
                    match sessionPrincipals.TryGetValue sessionId with
                    | true, p -> p
                    | _ -> runtimePrincipal

                let! _ =
                    subscriptions.SubscribeAsync(
                        sessionId,
                        principal,
                        sub.Filter,
                        sub.Since,
                        principalOf,
                        correlationId = env.Id
                    )

                return ()
            | Unsubscribe _ ->
                match env.SubscriptionId with
                | Some sid -> do! subscriptions.UnsubscribeAsync sid
                | None ->
                    let n = nack env.Id "INVALID_ARGUMENT" "unsubscribe requires subscription_id"
                    do! send n ct

                return ()
            | ArtifactPut ap ->
                let! result = artifactStore.PutAsync(sessionId, ap.MediaType, ap.Data, ?sha256 = ap.Sha256)

                match result with
                | Ok r ->
                    let env' =
                        Envelopes.artifactRef r
                        |> Envelope.withSession sessionId
                        |> Envelope.withCorrelation env.Id

                    do! send env' ct
                | Error err ->
                    let payload: ErrorPayload =
                        {
                            Code = ARCPError.code err
                            Message = ARCPError.message err
                            Retryable = Some(ARCPError.retryable err)
                            Details = None
                            Cause = None
                            TraceId = None
                        }

                    let env' =
                        Envelopes.toolError payload
                        |> Envelope.withSession sessionId
                        |> Envelope.withCorrelation env.Id

                    do! send env' ct

                return ()
            | ArtifactFetch af ->
                let! result = artifactStore.FetchAsync af.ArtifactId

                match result with
                | Ok a ->
                    let put: ArtifactPut =
                        {
                            MediaType = a.MediaType
                            Data = Convert.ToBase64String a.Data
                            Sha256 = Some a.Sha256
                        }

                    let env' =
                        Envelopes.artifactPut put
                        |> Envelope.withSession sessionId
                        |> Envelope.withCorrelation env.Id

                    do! send env' ct
                | Error err ->
                    let payload: ErrorPayload =
                        {
                            Code = ARCPError.code err
                            Message = ARCPError.message err
                            Retryable = Some(ARCPError.retryable err)
                            Details = None
                            Cause = None
                            TraceId = None
                        }

                    let env' =
                        Envelopes.toolError payload
                        |> Envelope.withSession sessionId
                        |> Envelope.withCorrelation env.Id

                    do! send env' ct

                return ()
            | ArtifactRelease ar ->
                let! result = artifactStore.ReleaseAsync ar.ArtifactId

                match result with
                | Ok _ ->
                    let env' =
                        Envelopes.ack { Message = None }
                        |> Envelope.withSession sessionId
                        |> Envelope.withCorrelation env.Id

                    do! send env' ct
                | Error err ->
                    let n = nack env.Id (ARCPError.code err) (ARCPError.message err)
                    do! send n ct

                return ()
            | Resume r ->
                // Phase 5: in-process resume only. Replay the session's log
                // strictly via the transport (do not re-log or re-fanout).
                let afterId = r.AfterMessageId

                let validCursor =
                    match afterId with
                    | None -> true
                    | Some mid ->
                        // Probe whether the cursor exists in the log: if Replay
                        // returns empty AND the log has any events for this
                        // session, treat as DATA_LOSS.
                        let events = eventLog.Replay(sessionId, mid) |> Seq.toList
                        let _ = events
                        // Determine cursor presence via total count vs after-only count.
                        let allEvents = eventLog.Replay sessionId |> Seq.toList

                        allEvents |> List.exists (fun e -> e.MessageId = mid)

                if not validCursor then
                    let payload: ErrorPayload =
                        {
                            Code = "DATA_LOSS"
                            Message = "after_message_id not in log"
                            Retryable = Some false
                            Details = None
                            Cause = None
                            TraceId = None
                        }

                    let env' =
                        Envelopes.toolError payload
                        |> Envelope.withSession sessionId
                        |> Envelope.withCorrelation env.Id

                    do! send env' ct
                    return ()
                else
                    let events =
                        match afterId with
                        | Some mid -> eventLog.Replay(sessionId, mid)
                        | None -> eventLog.Replay sessionId

                    for ev in events do
                        try
                            let parsed = JsonDocument.Parse(ev.EnvelopeJson).RootElement
                            let typed = Json.fromElement<Envelope<MessageType>> parsed
                            do! replaySend typed
                        with
                        | :? OperationCanceledException -> ()
                        | ex ->
                            logger.LogWarning(
                                ex,
                                "replay of message {MessageId} skipped due to deserialization failure",
                                ev.MessageId
                            )

                    return ()
            | _ ->
                logger.LogWarning(
                    "unhandled inbound message type {Type} (id {MessageId}); replying UNIMPLEMENTED",
                    env.Type,
                    env.Id
                )

                let n = nack env.Id "UNIMPLEMENTED" (sprintf "%s not implemented" env.Type)

                do! send n ct
                return ()
        }

    let loop (ct: CancellationToken) : Task<unit> =
        task {
            let mutable state = Unauthenticated
            let mutable running = true
            let mutable jobManager: JobManager option = None
            let mutable streamManager: StreamManager option = None

            while running && not ct.IsCancellationRequested do
                let! incoming =
                    task {
                        try
                            return! transport.ReceiveAsync ct
                        with
                        | :? OperationCanceledException -> return None
                        | ex ->
                            logger.LogWarning(ex, "transport receive failed")
                            return None
                    }

                match incoming with
                | None -> running <- false
                | Some env ->
                    // Log inbound symmetrically with outbound (RFC §19).
                    try
                        let! _ = eventLog.AppendAsync env
                        ()
                    with
                    | :? OperationCanceledException -> ()
                    | ex -> logger.LogWarning(ex, "inbound event log append failed for {MessageId}", env.Id)

                    match state with
                    | Unauthenticated ->
                        match env.Payload with
                        | SessionOpen openPayload ->
                            let! next = handleOpen env openPayload ct
                            state <- next

                            match next with
                            | Authenticated(principal, sid, _, _) ->
                                runtimePrincipal <- principal
                                sessionPrincipals.[sid] <- principal
                                currentSession <- Some sid

                                let jm =
                                    new JobManager(
                                        options.TimeProvider,
                                        options.LoggerFactory,
                                        options.HeartbeatInterval,
                                        options.MissedDeadlineLimit,
                                        sendIgnoring
                                    )

                                let sm = StreamManager(sendIgnoring, options.StreamCapacity)
                                jobManager <- Some jm
                                streamManager <- Some sm
                                activeJobManager <- Some jm
                                activeStreamManager <- Some sm
                                let _ = sid
                                ()
                            | Closed _ -> running <- false
                            | _ -> ()
                        | _ -> logger.LogWarning("dropping {Type} before session.open", env.Type)
                    | AwaitingAuthenticate _ ->
                        let! st = rejectAndClose "FAILED_PRECONDITION" "challenge flow not implemented" ct
                        state <- st
                        running <- false
                    | Authenticated(_, sid, _, _) ->
                        match jobManager, streamManager with
                        | Some jm, Some sm -> do! handleAuthenticated sid jm sm env ct
                        | _ -> ()

                        match env.Payload with
                        | SessionClose _ -> running <- false
                        | _ -> ()
                    | Closed _ -> running <- false

            return ()
        }

    /// <summary>Register an in-process tool handler.</summary>
    member _.RegisterTool(name: string, handler: ToolHandler) : unit = tools.[name] <- handler

    /// <summary>Lease manager owned by this runtime (RFC §15.5).</summary>
    member _.LeaseManager: LeaseManager = leaseManager

    /// <summary>Event log used for backfill and resume (RFC §13.2, §19).</summary>
    member _.EventLog: EventLog = eventLog

    /// <summary>Subscription manager owned by this runtime (RFC §13).</summary>
    member _.Subscriptions: SubscriptionManager = subscriptions

    /// <summary>Artifact store owned by this runtime (RFC §16).</summary>
    member _.ArtifactStore: ArtifactStore = artifactStore

    /// <summary>
    /// Request a free-form value from the human-in-the-loop (RFC §14.1).
    /// </summary>
    member _.RequestHumanInputAsync
        (
            jobId: JobId,
            sessionId: SessionId,
            prompt: string,
            responseSchema: JsonElement option,
            defaultValue: JsonElement option,
            expiresAt: DateTimeOffset,
            ?ct: CancellationToken
        ) : Task<Result<JsonElement, ARCPError>> =
        let ct = defaultArg ct CancellationToken.None
        requestHumanInputCore sessionId jobId prompt responseSchema defaultValue expiresAt ct

    /// <summary>
    /// Request a discrete choice from the human-in-the-loop (RFC §14.2).
    /// </summary>
    member _.RequestChoiceAsync
        (
            jobId: JobId,
            sessionId: SessionId,
            prompt: string,
            choiceOptions: ChoiceOption list,
            expiresAt: DateTimeOffset,
            ?ct: CancellationToken
        ) : Task<Result<string, ARCPError>> =
        let ct = defaultArg ct CancellationToken.None
        requestChoiceCore sessionId jobId prompt choiceOptions expiresAt ct

    /// <summary>
    /// Issue a permission challenge to the client and, on grant, allocate a
    /// lease (RFC §15.4, §15.5).
    /// </summary>
    member _.RequestPermissionAsync
        (
            jobId: JobId,
            sessionId: SessionId,
            permission: string,
            resource: string,
            operation: string,
            ?reason: string,
            ?leaseSeconds: int,
            ?ct: CancellationToken
        ) : Task<Result<Lease, ARCPError>> =
        let ct = defaultArg ct CancellationToken.None
        requestPermissionCore sessionId jobId permission resource operation reason leaseSeconds ct

    /// <summary>Begin dispatching. The returned task completes when the loop exits.</summary>
    member _.StartAsync(ct: CancellationToken) : Task =
        let linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token)
        loop linked.Token :> Task

    /// <summary>Request cancellation; the receive loop will exit on the next iteration.</summary>
    member _.StopAsync() : Task =
        try
            cts.Cancel()
        with _ ->
            ()

        try
            (leaseManager :> IDisposable).Dispose()
        with _ ->
            ()

        try
            (artifactStore :> IDisposable).Dispose()
        with _ ->
            ()

        try
            (subscriptions :> IDisposable).Dispose()
        with _ ->
            ()

        try
            match activeJobManager with
            | Some jm -> (jm :> IDisposable).Dispose()
            | None -> ()
        with _ ->
            ()

        Task.CompletedTask

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            ValueTask(
                task {
                    cts.Cancel()
                    (leaseManager :> IDisposable).Dispose()
                    (artifactStore :> IDisposable).Dispose()
                    (subscriptions :> IDisposable).Dispose()

                    match activeJobManager with
                    | Some jm -> (jm :> IDisposable).Dispose()
                    | None -> ()

                    match options.EventLog with
                    | Some _ -> ()
                    | None -> (eventLog :> IDisposable).Dispose()

                    do! transport.DisposeAsync()
                    cts.Dispose()
                }
            )
