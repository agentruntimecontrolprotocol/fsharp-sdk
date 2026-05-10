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
open ARCP.Auth.Auth
open ARCP.Transport
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
            string * JsonElement option * JsonElement option * DateTimeOffset * CancellationToken -> Task<JsonElement>
        /// <summary>
        /// Pick one of a fixed set of options (RFC §14.2). Returns the chosen
        /// option's id.
        /// </summary>
        RequestChoiceAsync: string * ChoiceOption list * DateTimeOffset * CancellationToken -> Task<string>
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

    let send (env: Envelope<MessageType>) (ct: CancellationToken) =
        task { do! transport.SendAsync(env, ct) }

    let sendIgnoring (env: Envelope<MessageType>) : Task =
        task {
            try
                do! transport.SendAsync(env, cts.Token)
            with _ ->
                ()
        }
        :> Task

    let leaseManager =
        new LeaseManager(options.TimeProvider, sendIgnoring, options.LeaseSweepInterval)

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

    let runtimePrincipal = ref "anonymous"

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
                -> Task<JsonElement>)
        (requestChoice: string -> ChoiceOption list -> DateTimeOffset -> CancellationToken -> Task<string>)
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
                    let contentTypeArg = defaultArg contentType null
                    let encodingArg = defaultArg encoding null

                    if isNull contentTypeArg && isNull encodingArg then
                        streamManager.OpenWriterAsync(kind, Some jobId)
                    elif isNull encodingArg then
                        streamManager.OpenWriterAsync(kind, Some jobId, contentType = contentTypeArg)
                    elif isNull contentTypeArg then
                        streamManager.OpenWriterAsync(kind, Some jobId, encoding = encodingArg)
                    else
                        streamManager.OpenWriterAsync(
                            kind,
                            Some jobId,
                            contentType = contentTypeArg,
                            encoding = encodingArg
                        )
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
        : Task<JsonElement> =
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
                return response.Value
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
                    return d
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
                    return raise (TimeoutException "human input deadline exceeded")
        }

    let requestChoiceCore
        (sessionId: SessionId)
        (jobId: JobId)
        (prompt: string)
        (choiceOptions: ChoiceOption list)
        (expiresAt: DateTimeOffset)
        (ct: CancellationToken)
        : Task<string> =
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
                return response.ChoiceId
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
                return raise (TimeoutException "human choice deadline exceeded")
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
                            !runtimePrincipal,
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
            | _ ->
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
                let! incoming = transport.ReceiveAsync ct

                match incoming with
                | None -> running <- false
                | Some env ->
                    match state with
                    | Unauthenticated ->
                        match env.Payload with
                        | SessionOpen openPayload ->
                            let! next = handleOpen env openPayload ct
                            state <- next

                            match next with
                            | Authenticated(principal, sid, _, _) ->
                                runtimePrincipal.Value <- principal

                                let jm =
                                    JobManager(
                                        options.TimeProvider,
                                        options.LoggerFactory,
                                        options.HeartbeatInterval,
                                        options.MissedDeadlineLimit,
                                        sendIgnoring
                                    )

                                let sm = StreamManager(sendIgnoring, options.StreamCapacity)
                                jobManager <- Some jm
                                streamManager <- Some sm
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
        ) : Task<JsonElement> =
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
        ) : Task<string> =
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
        cts.Cancel()
        Task.CompletedTask

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            ValueTask(
                task {
                    cts.Cancel()
                    (leaseManager :> IDisposable).Dispose()
                    do! transport.DisposeAsync()
                    cts.Dispose()
                }
            )
