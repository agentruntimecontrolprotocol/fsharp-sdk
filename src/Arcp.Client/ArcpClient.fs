namespace ARCP.Client

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client.Internal

/// ARCP client. Holds a transport, runs a single receive loop,
/// dispatches incoming envelopes to job handles or pending
/// responses, and exposes the public submit / subscribe / list /
/// ack / cancel API.
type ArcpClient(transport: ITransport, options: ArcpClientOptions) =
    let pending = PendingRegistry()
    let handles = ConcurrentDictionary<string, JobHandleWriter>()
    let mutable sessionCtx: SessionContext option = None
    let mutable autoAck: AutoAckScheduler option = None
    let mutable receiveLoopTask: Task = Task.CompletedTask
    let receiveLoopCts = new CancellationTokenSource()

    /// Attach a faulted-state observer so fire-and-forget sends do not
    /// become unobserved task exceptions (#60).
    let observeTask (t: Task) : unit =
        t.ContinueWith(
            (fun (tt: Task) ->
                if tt.IsFaulted then
                    eprintfn "[ARCP] background send failed: %O" tt.Exception),
            TaskContinuationOptions.OnlyOnFaulted
        )
        |> ignore

    let connectedTcs =
        TaskCompletionSource<SessionContext>(TaskCreationOptions.RunContinuationsAsynchronously)

    let requireFeature (flag: string) : Result<unit, ARCPError> =
        match sessionCtx with
        | Some s when s.NegotiatedFeatures.Contains flag -> Ok()
        | _ -> Error(ARCPError.InvalidRequest(sprintf "Feature %s was not negotiated" flag, None))

    let sendEnvelopeCt (env: Envelope) (ct: CancellationToken) : Task =
        let env =
            match sessionCtx with
            | Some s -> Envelope.withSessionId s.SessionId env
            | None -> env

        transport.SendAsync(env, ct)

    let sendEnvelope (env: Envelope) : Task = sendEnvelopeCt env receiveLoopCts.Token

    let sendMessage (msg: Message) : Task =
        let env = Codec.toEnvelope msg
        sendEnvelope env

    /// Await a correlated response while honoring the caller's token; on
    /// cancellation drop the pending entry so it does not leak (#98).
    let awaitResponse (requestId: string) (waiter: Task<Envelope>) (ct: CancellationToken) : Task<Envelope> =
        task {
            try
                return! waiter.WaitAsync(ct)
            with :? OperationCanceledException as ex ->
                pending.Remove requestId
                return raise ex
        }

    // Job-addressed envelopes can arrive before `SubmitAsync`/
    // `SubscribeAsync` register the handle (the receive loop completes
    // the request waiter and races ahead). Buffer such envelopes per
    // job id and flush them in order once the handle is registered, all
    // under one gate so registration and delivery cannot interleave (#95).
    let dispatchGate = obj ()
    let orphans = ConcurrentDictionary<string, ResizeArray<Envelope>>()

    let deliver (jid: string) (w: JobHandleWriter) (msg: Message) : unit =
        match msg with
        | Message.JobEvent payload ->
            match payload.Body with
            | JobEventBody.ResultChunk(rid, chunkSeq, data, enc, more) ->
                let assembler = w.ChunkIndex.GetOrCreate rid

                match assembler.Append(chunkSeq, data, enc, more) with
                | Ok _ -> w.Channel.Writer.TryWrite payload.Body |> ignore
                | Error err ->
                    // Out-of-order or undecodable chunk: tear down the
                    // handle so callers don't sit on a job that will never
                    // produce a usable result.
                    handles.TryRemove jid |> ignore
                    w.Channel.Writer.TryComplete() |> ignore
                    w.ResultSetter.TrySetResult(Error err) |> ignore
            | other -> w.Channel.Writer.TryWrite other |> ignore
        | Message.JobResult payload ->
            handles.TryRemove jid |> ignore
            w.Channel.Writer.TryComplete() |> ignore
            w.ResultSetter.TrySetResult(Ok payload) |> ignore
        | Message.JobError payload ->
            handles.TryRemove jid |> ignore

            let err =
                JobErrorMapper.ofWireWith payload.Code payload.Message payload.Details payload.Retryable (Some jid)

            w.Channel.Writer.TryComplete() |> ignore
            w.ResultSetter.TrySetResult(Error err) |> ignore
        | _ -> ()

    let dispatchJob (env: Envelope) (msg: Message) : unit =
        match env.JobId with
        | None -> ()
        | Some jid ->
            lock dispatchGate (fun () ->
                match handles.TryGetValue jid with
                | true, w -> deliver jid w msg
                | _ ->
                    // Buffer until the handle appears.
                    let q = orphans.GetOrAdd(jid, (fun _ -> ResizeArray<Envelope>()))
                    q.Add env)

    /// Register a job handle and flush any envelopes that arrived before
    /// it was known, preserving order (#95).
    let registerHandle (jid: string) (w: JobHandleWriter) : unit =
        lock dispatchGate (fun () ->
            handles.[jid] <- w

            match orphans.TryRemove jid with
            | true, q ->
                for env in q do
                    match Codec.toMessage env with
                    | Ok m ->
                        match handles.TryGetValue jid with
                        | true, w2 -> deliver jid w2 m
                        | _ -> ()
                    | _ -> ()
            | _ -> ())

    let onPing (payload: SessionPingPayload) : Task =
        let pong: SessionPongPayload =
            {
                PingNonce = payload.Nonce
                ReceivedAt = options.TimeProvider.GetUtcNow()
            }

        sendMessage (Message.SessionPong pong)

    let onEventSeq (env: Envelope) : unit =
        match env.EventSeq, autoAck with
        | Some seq, Some sched ->
            match sched.OnEvent seq with
            | Some toAck ->
                let ack: SessionAckPayload = { LastProcessedSeq = toAck }
                observeTask (sendMessage (Message.SessionAck ack))
            | None -> ()
        | _ -> ()

    let runReceiveLoop () : Task =
        task {
            let enumerable = transport.Receive(receiveLoopCts.Token)
            let enumerator = enumerable.GetAsyncEnumerator(receiveLoopCts.Token)

            try
                try
                    let mutable more = true

                    while more do
                        let! has = enumerator.MoveNextAsync().AsTask()

                        if not has then
                            more <- false
                        else
                            let env = enumerator.Current
                            pending.TryComplete(env.Id, env) |> ignore

                            match Codec.toMessage env with
                            | Error _ -> ()
                            | Ok msg ->
                                if Message.countsInEventSeq msg then
                                    onEventSeq env

                                match msg with
                                | Message.SessionPing p -> do! onPing p
                                | Message.JobEvent _
                                | Message.JobResult _
                                | Message.JobError _ -> dispatchJob env msg
                                | _ -> ()
                with
                | :? OperationCanceledException -> ()
                | ex -> pending.FailAll ex
            finally
                // §97: on any loop exit (clean EOF or cancellation) fault
                // every in-flight request waiter and complete every open
                // job handle so callers never hang forever.
                let closed = ARCPError.InternalError "ARCP transport closed"
                pending.FailAll(ArcpException closed)

                lock dispatchGate (fun () ->
                    for kv in handles do
                        kv.Value.Channel.Writer.TryComplete() |> ignore
                        kv.Value.ResultSetter.TrySetResult(Error closed) |> ignore

                    handles.Clear()
                    orphans.Clear())

            // §62: await enumerator disposal so transport teardown errors
            // surface rather than being swallowed on a background thread.
            do! enumerator.DisposeAsync()
        }
        :> Task

    let buildHello () : SessionHelloPayload =
        {
            Client = options.Client
            Auth = AuthPayload.ofScheme options.Auth
            Capabilities =
                {
                    Encodings = [ "json" ]
                    Features = options.Features
                }
        }

    let acceptWelcome (welcomeEnv: Envelope) (w: SessionWelcomePayload) : SessionContext =
        let sid =
            welcomeEnv.SessionId
            |> Option.map SessionId.ofString
            |> Option.defaultWith SessionId.newId

        let ctx =
            {
                SessionId = sid
                NegotiatedFeatures = Features.intersect options.Features w.Capabilities.Features
                HeartbeatIntervalSec = w.HeartbeatIntervalSec
                ResumeToken = w.ResumeToken
                ResumeWindowSec = w.ResumeWindowSec
                AgentInventory = w.Capabilities.Agents
            }

        sessionCtx <- Some ctx

        if ctx.NegotiatedFeatures.Contains Features.Ack then
            autoAck <- Some(AutoAckScheduler(options.AutoAck, options.TimeProvider))

        connectedTcs.TrySetResult ctx |> ignore
        ctx

    /// Send `session.hello` and await `session.welcome`. Starts the
    /// receive loop, then resolves with the negotiated session context.
    member this.ConnectAsync(ct: CancellationToken) : Task<SessionContext> =
        task {
            // §61: retain the receive-loop task so its completion (and any
            // fault) is observable via `Completion`.
            receiveLoopTask <- runReceiveLoop ()
            observeTask receiveLoopTask
            let env = Codec.toEnvelope (Message.SessionHello(buildHello ()))
            let waiter = pending.Register env.Id
            do! transport.SendAsync(env, ct)
            let! welcomeEnv = awaitResponse env.Id waiter ct

            match Codec.toMessage welcomeEnv with
            | Ok(Message.SessionWelcome w) -> return acceptWelcome welcomeEnv w
            | _ ->
                let err = ARCPError.InvalidRequest("Expected session.welcome", None)
                connectedTcs.TrySetException(ArcpException err) |> ignore
                return raise (ArcpException err)
        }

    /// Submit a new job and return a `JobHandle` for tracking it.
    member this.SubmitAsync(request: JobSubmitRequest, ct: CancellationToken) : Task<JobHandle> =
        task {
            let payload: JobSubmitPayload =
                {
                    Agent = request.Agent
                    Input = request.Input
                    LeaseRequest = request.LeaseRequest
                    LeaseConstraints = request.LeaseConstraints
                    IdempotencyKey = request.IdempotencyKey
                    MaxRuntimeSec = request.MaxRuntimeSec
                }

            match
                if payload.LeaseConstraints.IsSome then
                    requireFeature Features.LeaseExpiresAt
                else
                    Ok()
            with
            | Error e -> return raise (ArcpException e)
            | Ok() ->
                let env = Codec.toEnvelope (Message.JobSubmit payload)
                let waiter = pending.Register env.Id
                do! sendEnvelopeCt env ct
                let! acceptedEnv = awaitResponse env.Id waiter ct

                match Codec.toMessage acceptedEnv with
                | Ok(Message.JobAccepted accepted) ->
                    let jid = JobId.ofString accepted.JobId

                    let cancelDelegate (reason, ct') =
                        task {
                            let p: JobCancelPayload =
                                {
                                    JobId = accepted.JobId
                                    Reason = reason
                                }

                            do! sendMessage (Message.JobCancel p)
                            return Ok()
                        }

                    let credentials = accepted.Credentials |> Option.defaultValue []
                    let handle, writer = mkHandle jid credentials cancelDelegate
                    registerHandle accepted.JobId writer
                    return handle
                | Ok(Message.JobError errPayload) ->
                    // §71: no job id context here — pass None.
                    let err =
                        JobErrorMapper.ofWireWith
                            errPayload.Code
                            errPayload.Message
                            errPayload.Details
                            errPayload.Retryable
                            None

                    return raise (ArcpException err)
                | _ -> return raise (ArcpException(ARCPError.InvalidRequest("Expected job.accepted", None)))
        }

    /// Attach to an existing job (spec §7.6).
    member this.SubscribeAsync(jobId: JobId, options: SubscribeOptions, ct: CancellationToken) : Task<JobHandle> =
        task {
            match requireFeature Features.Subscribe with
            | Error e -> return raise (ArcpException e)
            | Ok() ->
                let payload: JobSubscribePayload =
                    {
                        JobId = jobId.Value
                        FromEventSeq = options.FromEventSeq
                        History = Some options.History
                    }

                let env = Codec.toEnvelope (Message.JobSubscribe payload)
                let waiter = pending.Register env.Id
                do! sendEnvelopeCt env ct
                let! subscribedEnv = awaitResponse env.Id waiter ct

                // §7.6 / #96: surface subscription denials instead of
                // returning a live-looking handle.
                match Codec.toMessage subscribedEnv with
                | Ok(Message.JobSubscribed _) ->
                    let cancelDelegate (_reason, _ct') =
                        task { return Error(ARCPError.PermissionDenied("Subscribers cannot cancel", None)) }

                    let handle, writer = mkHandle jobId [] cancelDelegate
                    registerHandle jobId.Value writer
                    return handle
                | Ok(Message.SessionError e) ->
                    return
                        raise (
                            ArcpException(JobErrorMapper.ofWireWith e.Code e.Message e.Details e.Retryable (Some jobId.Value))
                        )
                | _ -> return raise (ArcpException(ARCPError.InvalidRequest("Expected job.subscribed", None)))
        }

    /// Stop receiving events for a subscribed job.
    member this.UnsubscribeAsync(jobId: JobId, ct: CancellationToken) : Task =
        task {
            match requireFeature Features.Subscribe with
            | Error e -> raise (ArcpException e)
            | Ok() ->
                let payload: JobUnsubscribePayload = { JobId = jobId.Value }
                do! sendMessage (Message.JobUnsubscribe payload)

                match handles.TryRemove jobId.Value with
                | true, w -> w.Channel.Writer.TryComplete() |> ignore
                | _ -> ()
        }
        :> Task

    /// `session.list_jobs` → `session.jobs` (spec §6.6).
    member this.ListJobsAsync
        (filter: JobListFilter option, limit: int option, cursor: string option, ct: CancellationToken)
        : Task<SessionJobsPayload> =
        task {
            match requireFeature Features.ListJobs with
            | Error e -> return raise (ArcpException e)
            | Ok() ->
                let payload: SessionListJobsPayload =
                    {
                        Filter = filter
                        Limit = limit
                        Cursor = cursor
                    }

                let env = Codec.toEnvelope (Message.SessionListJobs payload)
                let waiter = pending.Register env.Id
                do! sendEnvelopeCt env ct
                let! respEnv = awaitResponse env.Id waiter ct

                match Codec.toMessage respEnv with
                | Ok(Message.SessionJobs jobs) -> return jobs
                | _ -> return raise (ArcpException(ARCPError.InvalidRequest("Expected session.jobs", None)))
        }

    /// Manually emit `session.ack`. Most callers don't need this —
    /// the auto-ack scheduler runs in the background when `ack` is
    /// negotiated.
    member this.AckAsync(lastProcessedSeq: int64, ct: CancellationToken) : Task =
        task {
            match requireFeature Features.Ack with
            | Error e -> raise (ArcpException e)
            | Ok() ->
                let payload: SessionAckPayload = { LastProcessedSeq = lastProcessedSeq }
                do! sendMessage (Message.SessionAck payload)
        }
        :> Task

    /// Negotiated feature set, or empty if not yet welcomed.
    member _.NegotiatedFeatures =
        sessionCtx
        |> Option.map (fun s -> s.NegotiatedFeatures)
        |> Option.defaultValue Set.empty

    member _.Session = sessionCtx

    /// Completes when the receive loop terminates (clean EOF, cancellation,
    /// or fault). Lets callers observe that the client stopped pumping (#61).
    member _.Completion: Task = receiveLoopTask

    /// Resolves with the negotiated session once `session.welcome` is
    /// received; faults if the handshake fails. A separate handle to the
    /// connect result for callers that did not await `ConnectAsync` (#70).
    member _.Connected: Task<SessionContext> = connectedTcs.Task

    /// Close the session cleanly with an optional reason.
    member this.CloseAsync(reason: string option, ct: CancellationToken) : Task =
        task {
            try
                do! sendMessage (Message.SessionClose { Reason = reason })
            with _ ->
                ()

            receiveLoopCts.Cancel()
            do! transport.CloseAsync ct
        }
        :> Task

    interface IDisposable with
        member this.Dispose() =
            try
                receiveLoopCts.Cancel()
            with _ ->
                ()

            try
                receiveLoopCts.Dispose()
            with _ ->
                ()
