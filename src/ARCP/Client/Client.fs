namespace ARCP.Client

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open FSharp.Control
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Session
open ARCP.Messages.Control
open ARCP.Messages.Execution
open ARCP.Messages.Human
open ARCP.Messages.Permissions
open ARCP.Messages.Registry
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime.Pending

/// <summary>
/// Outcome of a tool invocation: a result value or a terminal error.
/// </summary>
type ToolOutcome =
    /// <summary>Successful completion.</summary>
    | ToolOk of JsonElement option
    /// <summary>Terminal error.</summary>
    | ToolErr of ARCPError

/// <summary>
/// Minimal ARCP client that drives the session handshake (RFC §9), invokes
/// tools, cancels jobs, and exposes progress streams. A background receive
/// loop dispatches incoming envelopes to pending registries.
/// </summary>
type Client(transport: ITransport, scheme: AuthScheme) =

    let cts = new CancellationTokenSource()

    let mutable humanInputHandler: IHumanInputHandler option = None
    let mutable choiceHandler: IChoiceHandler option = None
    let mutable permissionHandler: IPermissionHandler option = None

    let pendingByCorrelation =
        ConcurrentDictionary<MessageId, TaskCompletionSource<ToolOutcome>>()

    let jobIdByCorrelation =
        ConcurrentDictionary<MessageId, TaskCompletionSource<JobId>>()

    let pendingCancelByJobId =
        ConcurrentDictionary<JobId, TaskCompletionSource<Result<unit, ARCPError>>>()

    let progressByJobId = ConcurrentDictionary<JobId, Channel<JobProgress>>()

    let buildAuthBlock () : AuthBlock =
        match scheme with
        | Bearer t ->
            {
                Scheme = "bearer"
                Token = Some t
                Fingerprint = None
            }
        | Jwt t ->
            {
                Scheme = "signed_jwt"
                Token = Some t
                Fingerprint = None
            }
        | Anonymous ->
            {
                Scheme = "none"
                Token = None
                Fingerprint = None
            }

    let errorOfPayload (p: ErrorPayload) : ARCPError =
        match p.Code with
        | "CANCELLED" -> Cancelled p.Message
        | "INVALID_ARGUMENT" -> InvalidArgument("", p.Message)
        | "DEADLINE_EXCEEDED" -> DeadlineExceeded p.Message
        | "NOT_FOUND" -> NotFound p.Message
        | "FAILED_PRECONDITION" -> FailedPrecondition p.Message
        | "ABORTED" -> Aborted p.Message
        | "UNIMPLEMENTED" -> Unimplemented p.Message
        | "INTERNAL" -> Internal(p.Message, None)
        | "UNAVAILABLE" -> Unavailable p.Message
        | "HEARTBEAT_LOST" -> Internal(p.Message, None)
        | "PERMISSION_DENIED" -> ARCPError.PermissionDenied("", p.Message)
        | _ -> Unknown p.Message

    let runReceiveLoop () : Task =
        task {
            try
                let mutable running = true

                while running && not cts.IsCancellationRequested do
                    let! env = transport.ReceiveAsync cts.Token

                    match env with
                    | None -> running <- false
                    | Some e ->
                        let corr = e.CorrelationId

                        match e.Payload with
                        | JobCompleted jc ->
                            match corr with
                            | Some c ->
                                match pendingByCorrelation.TryRemove c with
                                | true, tcs -> tcs.TrySetResult(ToolOk jc.Value) |> ignore
                                | _ -> ()
                            | None -> ()
                        | JobFailed jf ->
                            match corr with
                            | Some c ->
                                match pendingByCorrelation.TryRemove c with
                                | true, tcs -> tcs.TrySetResult(ToolErr(errorOfPayload jf)) |> ignore
                                | _ -> ()
                            | None -> ()
                        | JobCancelled jc ->
                            match corr with
                            | Some c ->
                                match pendingByCorrelation.TryRemove c with
                                | true, tcs ->
                                    tcs.TrySetResult(ToolErr(Cancelled(jc.Reason |> Option.defaultValue "cancelled")))
                                    |> ignore
                                | _ -> ()
                            | None -> ()
                        | JobAccepted ja ->
                            progressByJobId.GetOrAdd(ja.JobId, fun _ -> Channel.CreateUnbounded<JobProgress>())
                            |> ignore

                            match corr with
                            | Some c ->
                                match jobIdByCorrelation.TryGetValue c with
                                | true, tcs -> tcs.TrySetResult(ja.JobId) |> ignore
                                | _ -> ()
                            | None -> ()
                        | JobProgress p ->
                            match e.JobId with
                            | Some jid ->
                                let ch =
                                    progressByJobId.GetOrAdd(jid, fun _ -> Channel.CreateUnbounded<JobProgress>())

                                let! _ = ch.Writer.WriteAsync(p).AsTask()
                                ()
                            | None -> ()
                        | CancelAccepted ca ->
                            let jid = JobId.ofString ca.TargetId

                            match pendingCancelByJobId.TryRemove jid with
                            | true, tcs -> tcs.TrySetResult(Ok()) |> ignore
                            | _ -> ()
                        | CancelRefused cr ->
                            let jid = JobId.ofString cr.TargetId

                            match pendingCancelByJobId.TryRemove jid with
                            | true, tcs ->
                                tcs.TrySetResult(Error(FailedPrecondition(cr.Reason |> Option.defaultValue "refused")))
                                |> ignore
                            | _ -> ()
                        | HumanInputRequest req ->
                            match humanInputHandler with
                            | Some handler ->
                                let reqId = e.Id
                                let sid = e.SessionId
                                let jid = e.JobId

                                // Run handler asynchronously; do not block the receive loop.
                                let _ =
                                    Task.Run(fun () ->
                                        task {
                                            try
                                                let! value =
                                                    handler.HandleAsync(
                                                        req.Prompt,
                                                        req.ResponseSchema,
                                                        req.Default,
                                                        req.ExpiresAt,
                                                        cts.Token
                                                    )

                                                let responsePayload: HumanInputResponse =
                                                    {
                                                        Value = value
                                                        RespondedBy = Some "client"
                                                        RespondedAt = Some(DateTimeOffset.UtcNow)
                                                    }

                                                let mutable env =
                                                    Envelopes.humanInputResponse responsePayload
                                                    |> Envelope.withCorrelation reqId

                                                match sid with
                                                | Some s -> env <- env |> Envelope.withSession s
                                                | None -> ()

                                                match jid with
                                                | Some j -> env <- env |> Envelope.withJob j
                                                | None -> ()

                                                do! transport.SendAsync(env, cts.Token)
                                            with _ ->
                                                ()
                                        }
                                        :> Task)

                                ()
                            | None -> ()
                        | HumanChoiceRequest req ->
                            match choiceHandler with
                            | Some handler ->
                                let reqId = e.Id
                                let sid = e.SessionId
                                let jid = e.JobId

                                let _ =
                                    Task.Run(fun () ->
                                        task {
                                            try
                                                let! choiceId =
                                                    handler.HandleAsync(
                                                        req.Prompt,
                                                        req.Options,
                                                        req.ExpiresAt,
                                                        cts.Token
                                                    )

                                                let responsePayload: HumanChoiceResponse =
                                                    {
                                                        ChoiceId = choiceId
                                                        RespondedBy = Some "client"
                                                        RespondedAt = Some(DateTimeOffset.UtcNow)
                                                    }

                                                let mutable env =
                                                    Envelopes.humanChoiceResponse responsePayload
                                                    |> Envelope.withCorrelation reqId

                                                match sid with
                                                | Some s -> env <- env |> Envelope.withSession s
                                                | None -> ()

                                                match jid with
                                                | Some j -> env <- env |> Envelope.withJob j
                                                | None -> ()

                                                do! transport.SendAsync(env, cts.Token)
                                            with _ ->
                                                ()
                                        }
                                        :> Task)

                                ()
                            | None -> ()
                        | PermissionRequest req ->
                            match permissionHandler with
                            | Some handler ->
                                let reqId = e.Id
                                let sid = e.SessionId
                                let jid = e.JobId

                                let _ =
                                    Task.Run(fun () ->
                                        task {
                                            try
                                                let! decision =
                                                    handler.HandleAsync(
                                                        req.Permission,
                                                        req.Resource,
                                                        req.Operation,
                                                        req.Reason,
                                                        req.RequestedLeaseSeconds,
                                                        cts.Token
                                                    )

                                                let mutable env =
                                                    match decision with
                                                    | Grant ls ->
                                                        Envelopes.permissionGrant { LeaseSeconds = ls }
                                                        |> Envelope.withCorrelation reqId
                                                    | Deny reason ->
                                                        Envelopes.permissionDenied { Reason = reason }
                                                        |> Envelope.withCorrelation reqId

                                                match sid with
                                                | Some s -> env <- env |> Envelope.withSession s
                                                | None -> ()

                                                match jid with
                                                | Some j -> env <- env |> Envelope.withJob j
                                                | None -> ()

                                                do! transport.SendAsync(env, cts.Token)
                                            with _ ->
                                                ()
                                        }
                                        :> Task)

                                ()
                            | None -> ()
                        | _ -> ()
            with _ ->
                ()
        }
        :> Task

    let mutable receiveLoop: Task = Task.CompletedTask

    /// <summary>
    /// Perform <c>session.open</c> and await <c>session.accepted</c>. Any
    /// other terminal envelope (<c>session.rejected</c>,
    /// <c>session.unauthenticated</c>, or a closed transport) becomes an
    /// <see cref="ARCPError"/>.
    /// </summary>
    member _.OpenAsync(capabilities: Capabilities, ct: CancellationToken) : Task<Result<SessionId, ARCPError>> =
        task {
            let openPayload: SessionOpen =
                {
                    Arcp = Version.Protocol
                    Client =
                        {
                            Kind = "arcp-fsharp-client"
                            Version = Version.Sdk
                            Fingerprint = None
                            Principal = None
                        }
                    Auth = buildAuthBlock ()
                    Capabilities = capabilities
                }

            let env = Envelopes.sessionOpen openPayload
            do! transport.SendAsync(env, ct)
            let! reply = transport.ReceiveAsync ct

            match reply with
            | None -> return Error(Unavailable "transport closed before handshake response")
            | Some r ->
                match r.Payload with
                | SessionAccepted accepted ->
                    receiveLoop <- Task.Run(fun () -> runReceiveLoop ())
                    return Ok accepted.SessionId
                | SessionRejected rej ->
                    return Error(Unauthenticated(sprintf "%s: %s" rej.Code (rej.Reason |> Option.defaultValue "")))
                | SessionUnauthenticated rej ->
                    return Error(Unauthenticated(sprintf "%s: %s" rej.Code (rej.Reason |> Option.defaultValue "")))
                | _ -> return Error(InvalidArgument("envelope.type", sprintf "unexpected %s" r.Type))
        }

    /// <summary>
    /// Send <c>tool.invoke</c> and await a terminal job envelope correlated by
    /// <see cref="JobId"/>. Returns the result value or terminal error.
    /// </summary>
    member _.InvokeAsync
        (tool: string, arguments: JsonElement, ?ct: CancellationToken)
        : Task<Result<JsonElement option, ARCPError>> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let env = Envelopes.toolInvoke { Tool = tool; Arguments = arguments }
            let corr = env.Id

            let tcs =
                TaskCompletionSource<ToolOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            pendingByCorrelation.[corr] <- tcs

            let jobTcs =
                TaskCompletionSource<JobId>(TaskCreationOptions.RunContinuationsAsynchronously)

            jobIdByCorrelation.[corr] <- jobTcs

            do! transport.SendAsync(env, ct)

            use reg = ct.Register(fun () -> tcs.TrySetCanceled() |> ignore)
            let _ = reg

            try
                let! outcome = tcs.Task

                match outcome with
                | ToolOk v -> return Ok v
                | ToolErr e -> return Error e
            with :? OperationCanceledException ->
                return Error(Cancelled "client cancelled invoke")
        }

    /// <summary>
    /// Like <see cref="InvokeAsync"/> but also returns the assigned
    /// <see cref="JobId"/> once <c>job.accepted</c> arrives.
    /// </summary>
    member this.InvokeWithJobIdAsync
        (tool: string, arguments: JsonElement, ?ct: CancellationToken)
        : Task<JobId * Task<Result<JsonElement option, ARCPError>>> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let env = Envelopes.toolInvoke { Tool = tool; Arguments = arguments }
            let corr = env.Id

            let tcs =
                TaskCompletionSource<ToolOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)

            pendingByCorrelation.[corr] <- tcs

            let jobTcs =
                TaskCompletionSource<JobId>(TaskCreationOptions.RunContinuationsAsynchronously)

            jobIdByCorrelation.[corr] <- jobTcs

            do! transport.SendAsync(env, ct)

            let! jid = jobTcs.Task

            let resultTask =
                task {
                    try
                        let! outcome = tcs.Task

                        match outcome with
                        | ToolOk v -> return Ok v
                        | ToolErr e -> return Error e
                    with :? OperationCanceledException ->
                        return Error(Cancelled "client cancelled invoke")
                }

            return jid, resultTask
        }

    /// <summary>
    /// Send <c>cancel</c> targeting a job and await
    /// <c>cancel.accepted</c>/<c>cancel.refused</c>.
    /// </summary>
    member _.CancelAsync
        (jobId: JobId, ?reason: string, ?deadlineMs: int, ?ct: CancellationToken)
        : Task<Result<unit, ARCPError>> =
        let ct = defaultArg ct CancellationToken.None

        task {
            let tcs =
                TaskCompletionSource<Result<unit, ARCPError>>(TaskCreationOptions.RunContinuationsAsynchronously)

            pendingCancelByJobId.[jobId] <- tcs

            let env =
                Envelopes.cancel
                    {
                        Target = "job"
                        TargetId = JobId.value jobId
                        Reason = reason
                        DeadlineMs = deadlineMs
                    }
                |> Envelope.withJob jobId

            do! transport.SendAsync(env, ct)

            try
                let! r = tcs.Task
                return r
            with :? OperationCanceledException ->
                return Error(Cancelled "client cancelled cancel")
        }

    /// <summary>
    /// Subscribe to <c>job.progress</c> events for a job. The returned
    /// <see cref="IAsyncEnumerable{T}"/> ends when the underlying channel
    /// closes (e.g. the client disposes).
    /// </summary>
    member _.SubscribeProgress(jobId: JobId) : IAsyncEnumerable<JobProgress> =
        let ch =
            progressByJobId.GetOrAdd(jobId, fun _ -> Channel.CreateUnbounded<JobProgress>())

        taskSeq {
            let mutable running = true

            while running do
                let! has = ch.Reader.WaitToReadAsync(cts.Token)

                if not has then
                    running <- false
                else
                    match ch.Reader.TryRead() with
                    | true, p -> yield p
                    | _ -> ()
        }

    /// <summary>
    /// Install handlers for runtime-issued <c>human.input.request</c>,
    /// <c>human.choice.request</c>, and <c>permission.request</c> envelopes
    /// (RFC §14, §15.4). Returns the same client for fluent chaining.
    /// </summary>
    member this.WithHandlers
        (?humanInputHandler: IHumanInputHandler, ?choiceHandler: IChoiceHandler, ?permissionHandler: IPermissionHandler)
        : Client =
        match humanInputHandler with
        | Some h -> this.HumanInputHandler <- Some h
        | None -> ()

        match choiceHandler with
        | Some h -> this.ChoiceHandler <- Some h
        | None -> ()

        match permissionHandler with
        | Some h -> this.PermissionHandler <- Some h
        | None -> ()

        this

    /// <summary>Currently registered human-input handler (RFC §14.1).</summary>
    member _.HumanInputHandler
        with get () = humanInputHandler
        and set v = humanInputHandler <- v

    /// <summary>Currently registered choice handler (RFC §14.2).</summary>
    member _.ChoiceHandler
        with get () = choiceHandler
        and set v = choiceHandler <- v

    /// <summary>Currently registered permission handler (RFC §15.4).</summary>
    member _.PermissionHandler
        with get () = permissionHandler
        and set v = permissionHandler <- v

    interface System.IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    cts.Cancel()
                    do! transport.DisposeAsync()
                }
            )
