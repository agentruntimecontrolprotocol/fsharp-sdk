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
type ArcpServerOptions = {
    Runtime: RuntimeIdentity
    Features: Set<string>
    HeartbeatIntervalSec: int
    ResumeWindowSec: int
    BearerVerifier: IBearerVerifier
    TimeProvider: TimeProvider
}

[<RequireQualifiedAccess>]
module ArcpServerOptions =
    /// Sensible defaults: dev-mode bearer auth, every feature flag,
    /// 30s heartbeat, 600s resume window.
    let defaults : ArcpServerOptions = {
        Runtime = { Name = "arcp-fsharp-runtime"; Version = Version.Sdk }
        Features = Features.All
        HeartbeatIntervalSec = 30
        ResumeWindowSec = 600
        BearerVerifier = DevModeBearerVerifier() :> IBearerVerifier
        TimeProvider = TimeProvider.System
    }

/// ARCP runtime / server entry point.
///
/// `RegisterAgent` (or `RegisterAgentVersion` + `SetDefaultAgentVersion`)
/// installs agent handlers. `HandleSessionAsync` runs one accepted
/// transport — one per WebSocket connection or stdio child.
type ArcpServer(options: ArcpServerOptions) =
    let inventory = AgentInventoryStore()
    let eventLog =
        EventLog(
            { EventLogOptions.defaults with
                ResumeWindowSec = options.ResumeWindowSec
                TimeProvider = options.TimeProvider })
    let sessions = ConcurrentDictionary<string, ServerSessionContext>()
    let agentHandlers = ConcurrentDictionary<string, ArcpAgentHandler>()

    // The outbox is built per-session and stored in a ref cell so
    // `JobManager` can route emits back through it without circular
    // construction.
    let outbox : IJobOutbox ref = ref Unchecked.defaultof<IJobOutbox>
    let jobs =
        JobManager(
            options.TimeProvider,
            { new IJobOutbox with
                member _.EmitJobEventAsync(rec0, body) = (!outbox).EmitJobEventAsync(rec0, body)
                member _.EmitJobResultAsync(rec0, p) = (!outbox).EmitJobResultAsync(rec0, p)
                member _.EmitJobErrorAsync(rec0, p) = (!outbox).EmitJobErrorAsync(rec0, p) })

    let registerHandler (name: string) (version: string) (h: ArcpAgentHandler) =
        agentHandlers.[name + "@" + version] <- h
        let adapter : AgentHandler = fun _ -> task { return Unchecked.defaultof<JsonElement> }
        inventory.Register(name, version, adapter)

    /// Register an agent under the default version (`default`).
    member _.RegisterAgent(name: string, handler: ArcpAgentHandler) : unit =
        registerHandler name "default" handler

    /// Register a specific version of an agent (spec §7.5).
    member _.RegisterAgentVersion(name: string, version: string, handler: ArcpAgentHandler) : unit =
        registerHandler name version handler

    /// Pin the default version returned for bare `name` requests.
    member _.SetDefaultAgentVersion(name: string, version: string) : unit =
        inventory.SetDefault(name, version)

    member internal _.AgentInventoryStore = inventory
    member internal _.EventLog = eventLog
    member internal _.Jobs = jobs

    member private _.BuildOutbox() : IJobOutbox =
        { new IJobOutbox with
            member _.EmitJobEventAsync(record, body) =
                task {
                    do! EnvelopeOut.pushJobEvent sessions options.TimeProvider record.SessionId record.JobId body
                    for sid in jobs.Subscriptions.Subscribers record.JobId do
                        do! EnvelopeOut.pushJobEvent sessions options.TimeProvider sid record.JobId body
                    record.LastEventSeq <- record.LastEventSeq + 1L
                } :> Task
            member _.EmitJobResultAsync(record, payload) =
                task {
                    do! EnvelopeOut.pushJobResult sessions record.SessionId record.JobId payload
                    for sid in jobs.Subscriptions.Subscribers record.JobId do
                        do! EnvelopeOut.pushJobResult sessions sid record.JobId payload
                    jobs.Terminate(record.JobId, payload.FinalStatus)
                } :> Task
            member _.EmitJobErrorAsync(record, payload) =
                task {
                    do! EnvelopeOut.pushJobError sessions record.SessionId record.JobId payload
                    for sid in jobs.Subscriptions.Subscribers record.JobId do
                        do! EnvelopeOut.pushJobError sessions sid record.JobId payload
                    jobs.Terminate(record.JobId, JobStatus.Error)
                } :> Task }

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
                        transport options.Runtime options.BearerVerifier
                        options.TimeProvider eventLog options.Features
                        options.HeartbeatIntervalSec options.ResumeWindowSec
                        inventory env.Id hello ct
                match ctxOpt with
                | Some ctx ->
                    ctxRef.Value <- Some ctx
                    sessions.[ctx.SessionId.Value] <- ctx
                    return true
                | None -> return false
            | _, None -> return true
            | Message.SessionBye _, Some _ -> return false
            | Message.SessionPing p, Some ctx ->
                let pong: SessionPongPayload = {
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
                        options.TimeProvider inventory jobs agentHandlers
                        ctx env.Id submit env.TraceId ct
                return true
            | Message.JobCancel c, Some ctx ->
                match jobs.TryGet (JobId.ofString c.JobId) with
                | Some r when r.SessionId = ctx.SessionId ->
                    try r.Cancellation.Cancel() with _ -> ()
                | _ -> ()
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
            let ctxRef : ServerSessionContext option ref = ref None
            try
                let mutable more = true
                while more do
                    let! has = enumerator.MoveNextAsync().AsTask()
                    if not has then more <- false
                    else
                        let env = enumerator.Current
                        match Codec.toMessage env with
                        | Error _ -> ()
                        | Ok msg ->
                            let! keepGoing = this.DispatchMessage transport ctxRef env msg ct
                            if not keepGoing then more <- false
            with :? OperationCanceledException -> ()
            do! enumerator.DisposeAsync().AsTask()
            match ctxRef.Value with
            | Some ctx ->
                jobs.Subscriptions.UnsubscribeAll ctx.SessionId
                sessions.TryRemove ctx.SessionId.Value |> ignore
            | None -> ()
        } :> Task

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
                        ctx requestId
                        (ARCPError.InvalidRequest("list_jobs feature not negotiated", None)) ct
            else
                let filtered =
                    jobs.AllForPrincipal ctx.Principal.Id
                    |> Seq.filter (fun r ->
                        match req.Filter with
                        | None -> true
                        | Some f ->
                            (f.Status |> Option.map (List.contains r.Status) |> Option.defaultValue true)
                            && (f.Agent |> Option.map (fun a -> r.Agent = a) |> Option.defaultValue true)
                            && (f.CreatedAfter |> Option.map (fun ca -> r.CreatedAt >= ca) |> Option.defaultValue true))
                    |> Seq.toList
                let limited =
                    match req.Limit with
                    | Some n when n > 0 -> List.truncate n filtered
                    | _ -> filtered
                let resp: SessionJobsPayload = {
                    RequestId = requestId
                    Jobs = limited |> List.map jobs.ToSummary
                    NextCursor = None
                }
                let env =
                    Message.SessionJobs resp
                    |> Codec.toEnvelope
                    |> Envelope.withId requestId
                    |> Envelope.withSessionId ctx.SessionId
                do! ctx.Transport.SendAsync(env, ct)
        } :> Task

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
                        ctx requestId
                        (ARCPError.InvalidRequest("subscribe feature not negotiated", None)) ct
            else
                match jobs.TryGet (JobId.ofString sub.JobId) with
                | None ->
                    do! EnvelopeOut.respondWithError ctx requestId (ARCPError.JobNotFound sub.JobId) ct
                | Some record when record.Principal.Id <> ctx.Principal.Id ->
                    do!
                        EnvelopeOut.respondWithError
                            ctx requestId
                            (ARCPError.PermissionDenied("Subscribe denied", None)) ct
                | Some record ->
                    jobs.Subscriptions.Subscribe(record.JobId, ctx.SessionId)
                    let payload: JobSubscribedPayload = {
                        JobId = record.JobId.Value
                        CurrentStatus = record.Status
                        Agent = record.Agent
                        Lease = record.Lease
                        ParentJobId = record.ParentJobId
                        TraceId = record.TraceId
                        SubscribedFrom = record.LastEventSeq
                        Replayed = sub.History |> Option.defaultValue false
                    }
                    let env =
                        Message.JobSubscribed payload
                        |> Codec.toEnvelope
                        |> Envelope.withId requestId
                        |> Envelope.withSessionId ctx.SessionId
                        |> Envelope.withJobId record.JobId
                    do! ctx.Transport.SendAsync(env, ct)
        } :> Task
