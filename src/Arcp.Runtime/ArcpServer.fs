namespace ARCP.Runtime

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime.Auth
open ARCP.Runtime.Internal
open ARCP.Runtime.Store

/// Function the agent registers. Receives the `JobContext` and the
/// `input` JsonElement; returns the inline result (or
/// `Json.nullElement ()` if the agent streamed via `result_chunk`).
type ArcpAgentHandler = JobContext -> Task<JsonElement>

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
/// installs agent handlers. `HandleSessionAsync` runs a single
/// accepted transport (one per WebSocket connection or one per
/// stdio child).
type ArcpServer(options: ArcpServerOptions) =
    let inventory = AgentInventoryStore()
    let eventLog = EventLog({ EventLogOptions.defaults with ResumeWindowSec = options.ResumeWindowSec; TimeProvider = options.TimeProvider })
    let sessions = ConcurrentDictionary<string, ServerSessionContext>()

    // Outbox is built per `JobManager`; we need a shared one because
    // every session shares the same job map (subscriptions cross
    // sessions).
    let outbox =
        let inst = ref Unchecked.defaultof<IJobOutbox>
        inst

    let jobs = JobManager(options.TimeProvider, { new IJobOutbox with
        member _.EmitJobEventAsync(rec0, body) = (!outbox).EmitJobEventAsync(rec0, body)
        member _.EmitJobResultAsync(rec0, p) = (!outbox).EmitJobResultAsync(rec0, p)
        member _.EmitJobErrorAsync(rec0, p) = (!outbox).EmitJobErrorAsync(rec0, p) })

    let agentHandlers = ConcurrentDictionary<string, ArcpAgentHandler>()
    let registerHandler (name: string) (version: string) (h: ArcpAgentHandler) =
        agentHandlers.[name + "@" + version] <- h
        // Adapter to non-generic AgentHandler stored in inventory.
        let adapter : AgentHandler = fun ctxObj ->
            task {
                let ctx = ctxObj :?> JobContext * JsonElement
                let context, _input = ctx
                return! h context
            }
        inventory.Register(name, version, adapter)

    member _.RegisterAgent(name: string, handler: ArcpAgentHandler) : unit =
        registerHandler name "default" handler

    member _.RegisterAgentVersion(name: string, version: string, handler: ArcpAgentHandler) : unit =
        registerHandler name version handler

    member _.SetDefaultAgentVersion(name: string, version: string) : unit =
        inventory.SetDefault(name, version)

    member _.AgentInventoryStore = inventory
    member _.EventLog = eventLog
    member _.Jobs = jobs

    /// Spin a session: process `session.hello`, hold the session
    /// open for jobs, terminate on `session.bye` / transport close.
    member this.HandleSessionAsync(transport: ITransport, ct: CancellationToken) : Task =
        task {
            // Build a thin outbox bound to this session's send-path.
            // Multi-session emit (subscribers across sessions) loops
            // through every session whose subscription registry
            // includes the relevant job.
            outbox := {
                new IJobOutbox with
                    member _.EmitJobEventAsync(recObj, body) =
                        task {
                            let record = recObj :?> JobRecord
                            // Emit to the owning session.
                            do! this.PushJobEventTo(record.SessionId, record.JobId, body)
                            // And to every subscriber.
                            for sid in jobs.Subscriptions.Subscribers record.JobId do
                                do! this.PushJobEventTo(sid, record.JobId, body)
                            record.LastEventSeq <- record.LastEventSeq + 1L
                        } :> Task
                    member _.EmitJobResultAsync(recObj, payload) =
                        task {
                            let record = recObj :?> JobRecord
                            do! this.PushJobResultTo(record.SessionId, record.JobId, payload)
                            for sid in jobs.Subscriptions.Subscribers record.JobId do
                                do! this.PushJobResultTo(sid, record.JobId, payload)
                            jobs.Terminate(record.JobId, payload.FinalStatus)
                        } :> Task
                    member _.EmitJobErrorAsync(recObj, payload) =
                        task {
                            let record = recObj :?> JobRecord
                            do! this.PushJobErrorTo(record.SessionId, record.JobId, payload)
                            for sid in jobs.Subscriptions.Subscribers record.JobId do
                                do! this.PushJobErrorTo(sid, record.JobId, payload)
                            jobs.Terminate(record.JobId, JobStatus.Error)
                        } :> Task
            }

            let enumerable = transport.Receive(ct)
            let enumerator = enumerable.GetAsyncEnumerator(ct)
            let mutable sessionCtxOpt : ServerSessionContext option = None
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
                            match msg, sessionCtxOpt with
                            | Message.SessionHello hello, _ ->
                                let! ctxOpt = this.HandleHelloAsync(transport, env.Id, hello, ct)
                                match ctxOpt with
                                | Some ctx ->
                                    sessionCtxOpt <- Some ctx
                                    sessions.[ctx.SessionId.Value] <- ctx
                                | None -> more <- false
                            | _, None -> ()
                            | Message.SessionBye _, Some ctx ->
                                more <- false
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
                            | Message.SessionAck a, Some ctx ->
                                ctx.LastAckedSeq <- a.LastProcessedSeq
                            | Message.SessionListJobs req, Some ctx ->
                                do! this.HandleListJobsAsync(env.Id, ctx, req, ct)
                            | Message.JobSubmit submit, Some ctx ->
                                do! this.HandleJobSubmitAsync(env.Id, ctx, submit, env.TraceId, ct)
                            | Message.JobCancel c, Some ctx ->
                                this.HandleJobCancel(ctx, c)
                            | Message.JobSubscribe s, Some ctx ->
                                do! this.HandleJobSubscribeAsync(env.Id, ctx, s, ct)
                            | Message.JobUnsubscribe u, Some ctx ->
                                jobs.Subscriptions.Unsubscribe(JobId.ofString u.JobId, ctx.SessionId) |> ignore
                            | _ -> ()
            with
            | :? OperationCanceledException -> ()
            do! enumerator.DisposeAsync().AsTask()
            match sessionCtxOpt with
            | Some ctx ->
                jobs.Subscriptions.UnsubscribeAll ctx.SessionId
                sessions.TryRemove ctx.SessionId.Value |> ignore
            | None -> ()
        } :> Task

    // --- Handshake -------------------------------------------------------

    member private this.HandleHelloAsync(transport: ITransport, requestId: string, hello: SessionHelloPayload, ct: CancellationToken) : Task<ServerSessionContext option> =
        task {
            // Authenticate.
            let! authResult =
                task {
                    match hello.Auth.Scheme with
                    | "bearer" ->
                        match hello.Auth.Token with
                        | Some t -> return! options.BearerVerifier.VerifyAsync(t, ct)
                        | None ->
                            return Error (ARCPError.Unauthenticated "Missing bearer token")
                    | "none" ->
                        return Ok (AnonymousPrincipal() :> IPrincipal)
                    | other ->
                        return Error (ARCPError.Unauthenticated (sprintf "Unsupported auth scheme: %s" other))
                }
            match authResult with
            | Error err ->
                let envOut =
                    Message.SessionError {
                        Code = ARCPError.code err
                        Message = ARCPError.message err
                        Retryable = ARCPError.retryable err
                        Details = ARCPError.details err
                    }
                    |> Codec.toEnvelope
                    |> Envelope.withId requestId
                do! transport.SendAsync(envOut, ct)
                return None
            | Ok principal ->
                let negotiated = Features.intersect hello.Capabilities.Features options.Features
                let agentsShape =
                    if negotiated.Contains Features.AgentVersions then
                        AgentInventory.Rich (inventory.ToRichInventory())
                    else
                        AgentInventory.Flat (inventory.ToFlatInventory())
                let sid = SessionId.newId ()
                let resumeToken = (MessageId.newId ()).Value
                let heartbeat =
                    if negotiated.Contains Features.Heartbeat then Some options.HeartbeatIntervalSec
                    else None
                let now = options.TimeProvider.GetUtcNow()
                let ctx =
                    ServerSessionContext.create
                        sid principal negotiated heartbeat resumeToken options.ResumeWindowSec
                        transport eventLog now
                let welcome: SessionWelcomePayload = {
                    Runtime = options.Runtime
                    ResumeToken = resumeToken
                    ResumeWindowSec = options.ResumeWindowSec
                    HeartbeatIntervalSec = heartbeat
                    Capabilities = {
                        Encodings = [ "json" ]
                        Features = negotiated
                        Agents = agentsShape
                    }
                }
                let envOut =
                    Message.SessionWelcome welcome
                    |> Codec.toEnvelope
                    |> Envelope.withSessionId sid
                    |> Envelope.withId requestId
                do! transport.SendAsync(envOut, ct)
                return Some ctx
        }

    // --- Outbox helpers --------------------------------------------------

    member private this.PushEnvelope(sid: SessionId, env: Envelope, attachSeq: bool) : Task =
        task {
            match sessions.TryGetValue sid.Value with
            | true, sctx ->
                let env =
                    if attachSeq then
                        let entry = sctx.EventLog.Append(sid, env)
                        entry.Envelope
                    else
                        Envelope.withSessionId sid env
                do! sctx.Transport.SendAsync(env, CancellationToken.None)
            | _ -> ()
        } :> Task

    member private this.PushJobEventTo(sid: SessionId, jobId: JobId, body: JobEventBody) : Task =
        let payload: JobEventPayload = {
            Kind = JobEventBody.kind body
            Ts = options.TimeProvider.GetUtcNow()
            Body = body
        }
        let env =
            Message.JobEvent payload
            |> Codec.toEnvelope
            |> Envelope.withJobId jobId
            |> Envelope.withSessionId sid
        this.PushEnvelope(sid, env, attachSeq = true)

    member private this.PushJobResultTo(sid: SessionId, jobId: JobId, payload: JobResultPayload) : Task =
        let env =
            Message.JobResult payload
            |> Codec.toEnvelope
            |> Envelope.withJobId jobId
            |> Envelope.withSessionId sid
        this.PushEnvelope(sid, env, attachSeq = true)

    member private this.PushJobErrorTo(sid: SessionId, jobId: JobId, payload: JobErrorPayload) : Task =
        let env =
            Message.JobError payload
            |> Codec.toEnvelope
            |> Envelope.withJobId jobId
            |> Envelope.withSessionId sid
        this.PushEnvelope(sid, env, attachSeq = true)

    // --- session.list_jobs ----------------------------------------------

    member private this.HandleListJobsAsync(requestId: string, ctx: ServerSessionContext, req: SessionListJobsPayload, ct: CancellationToken) : Task =
        task {
            if not (ctx.NegotiatedFeatures.Contains Features.ListJobs) then
                let errPayload: SessionErrorPayload = {
                    Code = "INVALID_REQUEST"
                    Message = "list_jobs feature not negotiated"
                    Retryable = false
                    Details = None
                }
                let env =
                    Message.SessionError errPayload
                    |> Codec.toEnvelope
                    |> Envelope.withId requestId
                    |> Envelope.withSessionId ctx.SessionId
                do! ctx.Transport.SendAsync(env, ct)
            else
                let all =
                    jobs.AllForPrincipal ctx.Principal.Id
                    |> Seq.toList
                let filtered =
                    match req.Filter with
                    | None -> all
                    | Some f ->
                        all
                        |> List.filter (fun r ->
                            (f.Status |> Option.map (fun ss -> ss |> List.contains r.Status) |> Option.defaultValue true)
                            && (f.Agent |> Option.map (fun a -> r.Agent = a) |> Option.defaultValue true)
                            && (f.CreatedAfter |> Option.map (fun ca -> r.CreatedAt >= ca) |> Option.defaultValue true))
                let limited =
                    match req.Limit with
                    | Some n when n > 0 -> filtered |> List.truncate n
                    | _ -> filtered
                let summaries = limited |> List.map jobs.ToSummary
                let resp: SessionJobsPayload = {
                    RequestId = requestId
                    Jobs = summaries
                    NextCursor = None
                }
                let env =
                    Message.SessionJobs resp
                    |> Codec.toEnvelope
                    |> Envelope.withId requestId
                    |> Envelope.withSessionId ctx.SessionId
                do! ctx.Transport.SendAsync(env, ct)
        } :> Task

    // --- job.submit ------------------------------------------------------

    member private this.HandleJobSubmitAsync(requestId: string, ctx: ServerSessionContext, submit: JobSubmitPayload, traceIdOpt: string option, ct: CancellationToken) : Task =
        task {
            // Idempotency-key short-circuit.
            match submit.IdempotencyKey with
            | Some key when (jobs.LookupIdempotencyKey key).IsSome ->
                let existing = (jobs.LookupIdempotencyKey key).Value
                let acceptedReplay =
                    match jobs.TryGet (JobId.ofString existing) with
                    | Some r ->
                        {
                            JobId = r.JobId.Value
                            Lease = r.Lease
                            LeaseConstraints = r.Constraints
                            Budget = if r.Budgets.Snapshot() = Map.empty then None else Some (r.Budgets.Snapshot())
                            AcceptedAt = r.CreatedAt
                            TraceId = r.TraceId
                        }
                    | None ->
                        {
                            JobId = existing
                            Lease = Lease.empty
                            LeaseConstraints = None
                            Budget = None
                            AcceptedAt = options.TimeProvider.GetUtcNow()
                            TraceId = None
                        }
                let env =
                    Message.JobAccepted acceptedReplay
                    |> Codec.toEnvelope
                    |> Envelope.withId requestId
                    |> Envelope.withSessionId ctx.SessionId
                    |> Envelope.withJobId (JobId.ofString existing)
                do! ctx.Transport.SendAsync(env, ct)
            | _ ->
                // Resolve agent.
                match inventory.Resolve submit.Agent with
                | Error err ->
                    do! this.RespondWithError(ctx, requestId, err, ct)
                | Ok (name, version, _adapter) ->
                    let resolvedAgent = AgentRef.format name (Some version)
                    let lease = submit.LeaseRequest |> Option.defaultValue Lease.empty
                    // Validate `lease_constraints.expires_at` is UTC + future.
                    let constraintsResult =
                        match submit.LeaseConstraints with
                        | None -> Ok None
                        | Some c when c.ExpiresAt.Offset <> TimeSpan.Zero ->
                            Error (ARCPError.InvalidRequest("lease_constraints.expires_at must be UTC", None))
                        | Some c when c.ExpiresAt <= options.TimeProvider.GetUtcNow() ->
                            Error (ARCPError.InvalidRequest("lease_constraints.expires_at must be in the future", None))
                        | Some c -> Ok (Some c)
                    match constraintsResult with
                    | Error err -> do! this.RespondWithError(ctx, requestId, err, ct)
                    | Ok constraints ->
                        let jobId = JobId.newId ()
                        let budgets = BudgetCounters()
                        budgets.SetInitial(Lease.initialBudgets lease)
                        let cts = new CancellationTokenSource()
                        let watchdog =
                            constraints
                            |> Option.map (fun c ->
                                let w = new ExpiryWatchdog(options.TimeProvider)
                                w.Start(c.ExpiresAt, fun () ->
                                    let payload: JobErrorPayload = {
                                        FinalStatus = JobStatus.Error
                                        Code = "LEASE_EXPIRED"
                                        Message = sprintf "Lease expired at %O" c.ExpiresAt
                                        Retryable = false
                                        Details = None
                                    }
                                    match jobs.TryGet jobId with
                                    | Some r -> ignore (jobs.EmitErrorAsync(r, payload))
                                    | None -> ())
                                w)
                        // Claim idempotency key (after resolution succeeds).
                        match submit.IdempotencyKey with
                        | Some key ->
                            match jobs.TryClaimIdempotencyKey(key, jobId) with
                            | Error err -> do! this.RespondWithError(ctx, requestId, err, ct)
                            | Ok () -> ()
                        | None -> ()

                        let record : JobRecord = {
                            JobId = jobId
                            SessionId = ctx.SessionId
                            Principal = ctx.Principal
                            Agent = resolvedAgent
                            Lease = lease
                            Constraints = constraints
                            Budgets = budgets
                            ParentJobId = None
                            TraceId = traceIdOpt
                            CreatedAt = options.TimeProvider.GetUtcNow()
                            Cancellation = cts
                            Watchdog = watchdog
                            Status = JobStatus.Pending
                            LastEventSeq = 0L
                        }
                        jobs.Register record

                        // Respond with job.accepted.
                        let initialBudget =
                            if budgets.Snapshot() = Map.empty then None
                            else Some (budgets.Snapshot())
                        let accepted: JobAcceptedPayload = {
                            JobId = jobId.Value
                            Lease = lease
                            LeaseConstraints = constraints
                            Budget = initialBudget
                            AcceptedAt = record.CreatedAt
                            TraceId = traceIdOpt
                        }
                        let env =
                            Message.JobAccepted accepted
                            |> Codec.toEnvelope
                            |> Envelope.withId requestId
                            |> Envelope.withSessionId ctx.SessionId
                            |> Envelope.withJobId jobId
                        do! ctx.Transport.SendAsync(env, ct)

                        // Launch agent in the background.
                        match agentHandlers.TryGetValue resolvedAgent with
                        | true, handler ->
                            this.LaunchJob(record, handler, submit.Input)
                        | _ ->
                            let err = ARCPError.AgentNotAvailable resolvedAgent
                            do! this.RespondWithError(ctx, requestId, err, ct)
        } :> Task

    member private this.LaunchJob(record: JobRecord, handler: ArcpAgentHandler, input: JsonElement) : unit =
        let onCostMetric (currency: string, amount: decimal) =
            record.Budgets.TryDecrement(currency, amount) |> ignore
        let emit (body: JobEventBody) : Task =
            jobs.EmitEventAsync(record, body)
        let beginStream () : ResultId = ResultId.newId ()
        let context =
            JobContext(
                record.JobId,
                record.SessionId,
                record.Lease,
                record.Constraints,
                record.Budgets,
                options.TimeProvider,
                record.Cancellation.Token,
                emit,
                beginStream,
                onCostMetric)
        record.Status <- JobStatus.Running
        Task.Run(fun () ->
            task {
                try
                    let! result = handler context
                    let payload: JobResultPayload = {
                        FinalStatus = JobStatus.Success
                        Result = Some result
                        ResultId = None
                        ResultSize = None
                        Summary = None
                    }
                    do! jobs.EmitResultAsync(record, payload)
                with
                | :? OperationCanceledException ->
                    let payload: JobResultPayload = {
                        FinalStatus = JobStatus.Cancelled
                        Result = None
                        ResultId = None
                        ResultSize = None
                        Summary = Some "cancelled"
                    }
                    do! jobs.EmitResultAsync(record, payload)
                | :? ArcpException as ax ->
                    let e = ax.Error
                    let payload: JobErrorPayload = {
                        FinalStatus = JobStatus.Error
                        Code = ARCPError.code e
                        Message = ARCPError.message e
                        Retryable = ARCPError.retryable e
                        Details = ARCPError.details e
                    }
                    do! jobs.EmitErrorAsync(record, payload)
                | ex ->
                    let payload: JobErrorPayload = {
                        FinalStatus = JobStatus.Error
                        Code = "INTERNAL_ERROR"
                        Message = ex.Message
                        Retryable = true
                        Details = None
                    }
                    do! jobs.EmitErrorAsync(record, payload)
            } :> Task) |> ignore

    member private this.HandleJobCancel(ctx: ServerSessionContext, cancel: JobCancelPayload) : unit =
        match jobs.TryGet (JobId.ofString cancel.JobId) with
        | Some r when r.SessionId = ctx.SessionId ->
            try r.Cancellation.Cancel() with _ -> ()
        | _ -> ()

    member private this.HandleJobSubscribeAsync(requestId: string, ctx: ServerSessionContext, sub: JobSubscribePayload, ct: CancellationToken) : Task =
        task {
            if not (ctx.NegotiatedFeatures.Contains Features.Subscribe) then
                let err = ARCPError.InvalidRequest("subscribe feature not negotiated", None)
                do! this.RespondWithError(ctx, requestId, err, ct)
            else
                match jobs.TryGet (JobId.ofString sub.JobId) with
                | None ->
                    do! this.RespondWithError(ctx, requestId, ARCPError.JobNotFound sub.JobId, ct)
                | Some record when record.Principal.Id <> ctx.Principal.Id ->
                    do! this.RespondWithError(
                            ctx, requestId,
                            ARCPError.PermissionDenied("Subscribe denied", None), ct)
                | Some record ->
                    jobs.Subscriptions.Subscribe(record.JobId, ctx.SessionId)
                    let history = sub.History |> Option.defaultValue false
                    let payload: JobSubscribedPayload = {
                        JobId = record.JobId.Value
                        CurrentStatus = record.Status
                        Agent = record.Agent
                        Lease = record.Lease
                        ParentJobId = record.ParentJobId
                        TraceId = record.TraceId
                        SubscribedFrom = record.LastEventSeq
                        Replayed = history
                    }
                    let env =
                        Message.JobSubscribed payload
                        |> Codec.toEnvelope
                        |> Envelope.withId requestId
                        |> Envelope.withSessionId ctx.SessionId
                        |> Envelope.withJobId record.JobId
                    do! ctx.Transport.SendAsync(env, ct)
        } :> Task

    member private this.RespondWithError(ctx: ServerSessionContext, requestId: string, err: ARCPError, ct: CancellationToken) : Task =
        let payload: SessionErrorPayload = {
            Code = ARCPError.code err
            Message = ARCPError.message err
            Retryable = ARCPError.retryable err
            Details = ARCPError.details err
        }
        let env =
            Message.SessionError payload
            |> Codec.toEnvelope
            |> Envelope.withId requestId
            |> Envelope.withSessionId ctx.SessionId
        ctx.Transport.SendAsync(env, ct)
