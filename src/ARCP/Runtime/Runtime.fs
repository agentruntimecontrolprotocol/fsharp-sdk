namespace ARCP.Runtime

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Session
open ARCP.Messages.Control
open ARCP.Messages.Execution
open ARCP.Messages.Streaming
open ARCP.Messages.Registry
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime.Session

/// <summary>
/// Signature for an in-process tool handler. Returns a result task that the
/// runtime converts into a <c>job.completed</c> / <c>job.failed</c> envelope.
/// </summary>
type ToolHandler = JsonElement -> CancellationToken -> Task<Result<JsonElement, ARCPError>>

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
        }

/// <summary>
/// ARCP runtime (RFC §9, §10, §11). Drives the session handshake then
/// dispatches subsequent envelopes to the job and stream subsystems.
/// </summary>
type Runtime(transport: ITransport, validator: IAuthValidator, logger: ILogger, options: RuntimeOptions) =

    let cts = new CancellationTokenSource()

    let tools = ConcurrentDictionary<string, ToolHandler>()

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

                let run (innerCt: CancellationToken) =
                    task {
                        let! r = handler ti.Arguments innerCt
                        return r
                    }

                let! _jid = jobManager.AcceptAsync(sessionId, ti.Tool, run, correlationId = env.Id)
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
                            | Authenticated(_, sid, _, _) ->
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
                    do! transport.DisposeAsync()
                    cts.Dispose()
                }
            )
