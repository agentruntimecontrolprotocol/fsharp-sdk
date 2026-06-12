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
    let receiveLoopCts = new CancellationTokenSource()

    let connectedTcs =
        TaskCompletionSource<SessionContext>(TaskCreationOptions.RunContinuationsAsynchronously)

    let requireFeature (flag: string) : Result<unit, ARCPError> =
        match sessionCtx with
        | Some s when s.NegotiatedFeatures.Contains flag -> Ok()
        | _ -> Error(ARCPError.InvalidRequest(sprintf "Feature %s was not negotiated" flag, None))

    let sendEnvelope (env: Envelope) : Task =
        let env =
            match sessionCtx with
            | Some s -> Envelope.withSessionId s.SessionId env
            | None -> env

        transport.SendAsync(env, receiveLoopCts.Token)

    let sendMessage (msg: Message) : Task =
        let env = Codec.toEnvelope msg
        sendEnvelope env

    let dispatchJobEvent (env: Envelope) (payload: JobEventPayload) : unit =
        match env.JobId with
        | None -> ()
        | Some jid ->
            match handles.TryGetValue jid with
            | true, w ->
                match payload.Body with
                | JobEventBody.ResultChunk(rid, chunkSeq, data, enc, more) ->
                    let assembler = w.ChunkIndex.GetOrCreate rid

                    match assembler.Append(chunkSeq, data, enc, more) with
                    | Ok _ -> w.Channel.Writer.TryWrite payload.Body |> ignore
                    | Error err ->
                        // Out-of-order or undecodable chunk: tear down
                        // the handle so callers don't sit on a job that
                        // will never produce a usable result.
                        handles.TryRemove jid |> ignore
                        w.Channel.Writer.TryComplete() |> ignore
                        w.ResultSetter.TrySetResult(Error err) |> ignore
                | other -> w.Channel.Writer.TryWrite other |> ignore
            | _ -> ()

    let dispatchJobResult (env: Envelope) (payload: JobResultPayload) : unit =
        match env.JobId with
        | None -> ()
        | Some jid ->
            match handles.TryRemove jid with
            | true, w ->
                w.Channel.Writer.TryComplete() |> ignore
                w.ResultSetter.TrySetResult(Ok payload) |> ignore
            | _ -> ()

    let dispatchJobError (env: Envelope) (payload: JobErrorPayload) : unit =
        match env.JobId with
        | None -> ()
        | Some jid ->
            match handles.TryRemove jid with
            | true, w ->
                let err = JobErrorMapper.ofWire payload.Code payload.Message payload.Details jid
                w.Channel.Writer.TryComplete() |> ignore
                w.ResultSetter.TrySetResult(Error err) |> ignore
            | _ -> ()

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
                ignore (sendMessage (Message.SessionAck ack))
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
                                | Message.JobEvent p -> dispatchJobEvent env p
                                | Message.JobResult p -> dispatchJobResult env p
                                | Message.JobError p -> dispatchJobError env p
                                | _ -> ()
                with
                | :? OperationCanceledException -> ()
                | ex -> pending.FailAll ex
            finally
                ignore (enumerator.DisposeAsync().AsTask())
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
            Resume = None
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
            ignore (runReceiveLoop ())
            let env = Codec.toEnvelope (Message.SessionHello(buildHello ()))
            let waiter = pending.Register env.Id
            do! transport.SendAsync(env, ct)
            let! welcomeEnv = waiter

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
                do! sendEnvelope env
                let! acceptedEnv = waiter

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
                    handles.[accepted.JobId] <- writer
                    return handle
                | Ok(Message.JobError errPayload) ->
                    let err =
                        JobErrorMapper.ofWire errPayload.Code errPayload.Message errPayload.Details ""

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
                do! sendEnvelope env
                let! _subscribed = waiter

                let cancelDelegate (_reason, _ct') =
                    task { return Error(ARCPError.PermissionDenied("Subscribers cannot cancel", None)) }

                let handle, writer = mkHandle jobId [] cancelDelegate
                handles.[jobId.Value] <- writer
                return handle
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
                do! sendEnvelope env
                let! respEnv = waiter

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
