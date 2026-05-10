namespace ARCP.Runtime

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Microsoft.Extensions.Logging
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Control
open ARCP.Messages.Execution
open ARCP.Messages.Human
open ARCP.Messages.Registry

/// <summary>
/// Job lifecycle (RFC §10.3 – §10.5). The <see cref="JobManager"/> tracks
/// state per <see cref="JobId"/>, emits lifecycle envelopes via a supplied
/// <c>send</c> callback, and supervises in-process runners with heartbeats
/// and cancellation.
/// </summary>
module Job =

    /// <summary>States a job can be in over its lifetime.</summary>
    type JobState =
        /// <summary>Accepted; not yet running.</summary>
        | Accepted
        /// <summary>Queued behind other work.</summary>
        | Queued
        /// <summary>Currently executing.</summary>
        | Running
        /// <summary>Blocked waiting on an external dependency (e.g. human input).</summary>
        | Blocked of reason: string
        /// <summary>Paused by the runtime.</summary>
        | Paused
        /// <summary>Terminal: successful completion.</summary>
        | Completed of value: JsonElement option
        /// <summary>Terminal: failure with error.</summary>
        | Failed of error: ARCPError
        /// <summary>Terminal: cancelled.</summary>
        | Cancelled of reason: string option

    /// <summary>True for terminal states.</summary>
    let isTerminal =
        function
        | Completed _
        | Failed _
        | Cancelled _ -> true
        | _ -> false

    /// <summary>Origin of a job's heartbeats: in-process timer vs. wire.</summary>
    type JobOrigin =
        /// <summary>Manager emits heartbeats; no watchdog.</summary>
        | InProcess
        /// <summary>External runner emits heartbeats over the wire; watchdog monitors.</summary>
        | External

    /// <summary>
    /// Internal job record. Mutable fields are guarded by the job manager's
    /// per-job locking discipline (one task per job).
    /// </summary>
    type Job =
        {
            JobId: JobId
            SessionId: SessionId
            Tool: string
            mutable State: JobState
            mutable StartedAt: DateTimeOffset option
            mutable LastHeartbeatAt: DateTimeOffset option
            mutable HeartbeatSequence: int
            CancellationSource: CancellationTokenSource
            /// <summary>Alias for <see cref="CancellationSource"/> (linked).</summary>
            Cts: CancellationTokenSource
            Origin: JobOrigin
            HeartbeatChannel: Channel<unit>
            CorrelationId: MessageId option
        }

/// <summary>
/// Tracks a session's jobs. Emits lifecycle envelopes via <paramref name="send"/>
/// and supervises both in-process and external runners.
/// </summary>
type JobManager
    (
        timeProvider: TimeProvider,
        loggerFactory: ILoggerFactory option,
        heartbeatInterval: TimeSpan,
        missedDeadlineLimit: int,
        send: Envelope<MessageType> -> Task
    ) =

    let logger: ILogger =
        match loggerFactory with
        | Some lf -> lf.CreateLogger("ARCP.Runtime.JobManager")
        | None -> Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance :> ILogger

    let jobs = ConcurrentDictionary<JobId, Job.Job>()

    let errorPayloadOf (err: ARCPError) : ErrorPayload =
        {
            Code = ARCPError.code err
            Message = ARCPError.message err
            Retryable = Some(ARCPError.retryable err)
            Details = None
            Cause = None
            TraceId = None
        }

    let emit (env: Envelope<MessageType>) : Task = send env

    let withCorr (corr: MessageId option) (env: Envelope<MessageType>) : Envelope<MessageType> =
        match corr with
        | Some c -> env |> Envelope.withCorrelation c
        | None -> env

    let emitAccepted (job: Job.Job) (corr: MessageId option) =
        let env =
            Envelopes.jobAccepted { JobId = job.JobId }
            |> Envelope.withSession job.SessionId
            |> Envelope.withJob job.JobId
            |> withCorr corr

        emit env

    let emitStarted (job: Job.Job) (corr: MessageId option) =
        let env =
            Envelopes.jobStarted { JobId = job.JobId }
            |> Envelope.withSession job.SessionId
            |> Envelope.withJob job.JobId
            |> withCorr corr

        emit env

    let emitHeartbeat (job: Job.Job) =
        job.HeartbeatSequence <- job.HeartbeatSequence + 1
        job.LastHeartbeatAt <- Some(timeProvider.GetUtcNow())

        let payload: JobHeartbeat =
            {
                Sequence = job.HeartbeatSequence
                DeadlineMs = int heartbeatInterval.TotalMilliseconds * (missedDeadlineLimit + 1)
                State =
                    match job.State with
                    | Job.Running -> "running"
                    | Job.Blocked _ -> "blocked"
                    | Job.Paused -> "paused"
                    | _ -> "running"
            }

        let env =
            Envelopes.jobHeartbeat payload
            |> Envelope.withSession job.SessionId
            |> Envelope.withJob job.JobId

        emit env

    let emitCompleted (job: Job.Job) (corr: MessageId option) (value: JsonElement option) =
        let env =
            Envelopes.jobCompleted { Value = value; ResultRef = None }
            |> Envelope.withSession job.SessionId
            |> Envelope.withJob job.JobId
            |> withCorr corr

        emit env

    let emitFailed (job: Job.Job) (corr: MessageId option) (err: ARCPError) =
        let env =
            Envelopes.jobFailed (errorPayloadOf err)
            |> Envelope.withSession job.SessionId
            |> Envelope.withJob job.JobId
            |> withCorr corr

        emit env

    let emitCancelled (job: Job.Job) (corr: MessageId option) (reason: string option) =
        let env =
            Envelopes.jobCancelled { Reason = reason }
            |> Envelope.withSession job.SessionId
            |> Envelope.withJob job.JobId
            |> withCorr corr

        emit env

    let runInProcessHeartbeatLoop (job: Job.Job) : Task =
        task {
            try
                while not job.Cts.IsCancellationRequested && not (Job.isTerminal job.State) do
                    do! Task.Delay(heartbeatInterval, timeProvider, job.Cts.Token)

                    if not (Job.isTerminal job.State) then
                        do! emitHeartbeat job
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.LogWarning(ex, "heartbeat loop terminated")
        }
        :> Task

    let runWatchdog (job: Job.Job) : Task =
        task {
            try
                let mutable missed = 0
                let mutable running = true

                while running && not job.Cts.IsCancellationRequested && not (Job.isTerminal job.State) do
                    let reader = job.HeartbeatChannel.Reader

                    let readTask = reader.WaitToReadAsync(job.Cts.Token).AsTask()

                    let delayTask = Task.Delay(heartbeatInterval, timeProvider, job.Cts.Token)

                    let! completed = Task.WhenAny(readTask :> Task, delayTask)

                    if completed = (readTask :> Task) then
                        missed <- 0

                        let! _ = readTask
                        reader.TryRead() |> ignore
                    else
                        missed <- missed + 1

                        if missed >= missedDeadlineLimit then
                            let err = HeartbeatLost(job.JobId, missed)
                            job.State <- Job.Failed err
                            do! emitFailed job job.CorrelationId err
                            running <- false
            with
            | :? OperationCanceledException -> ()
            | ex -> logger.LogWarning(ex, "watchdog terminated")
        }
        :> Task

    let runJob (job: Job.Job) (run: CancellationToken -> Task<Result<JsonElement, ARCPError>>) : Task =
        task {
            try
                do! emitAccepted job job.CorrelationId
                job.State <- Job.Running
                job.StartedAt <- Some(timeProvider.GetUtcNow())
                do! emitStarted job job.CorrelationId

                // start watchdog/heartbeat as fire-and-forget supervisors
                match job.Origin with
                | Job.InProcess ->
                    let _ = Task.Run(fun () -> runInProcessHeartbeatLoop job)
                    ()
                | Job.External ->
                    let _ = Task.Run(fun () -> runWatchdog job)
                    ()

                let! result =
                    task {
                        try
                            let! r = run job.Cts.Token
                            return r
                        with
                        | :? OperationCanceledException -> return Error(Cancelled "job cancelled")
                        | ex -> return Error(Internal(ex.Message, Some ex))
                    }

                match job.State with
                | Job.Cancelled _ ->
                    // already terminal cancelled; do not overwrite
                    ()
                | Job.Failed _ ->
                    // already terminal failed (e.g. heartbeat lost)
                    ()
                | _ ->
                    match result with
                    | Ok value ->
                        job.State <- Job.Completed(Some value)
                        do! emitCompleted job job.CorrelationId (Some value)
                    | Error err ->
                        match err with
                        | Cancelled _ ->
                            job.State <- Job.Cancelled None
                            do! emitCancelled job job.CorrelationId None
                        | _ ->
                            job.State <- Job.Failed err
                            do! emitFailed job job.CorrelationId err
            with ex ->
                let err = Internal(ex.Message, Some ex)
                job.State <- Job.Failed err
                do! emitFailed job job.CorrelationId err

            // Cancel the job CTS so heartbeat/watchdog loops exit immediately
            // rather than waiting out their next interval. We do not dispose
            // here because outstanding tasks may still observe the token.
            try
                if not job.Cts.IsCancellationRequested then
                    job.Cts.Cancel()
            with _ ->
                ()
        }
        :> Task

    /// <summary>
    /// Register a new job, emit <c>job.accepted</c> + <c>job.started</c>, and
    /// spawn the supervised runner. Returns the <see cref="JobId"/>
    /// immediately.
    /// </summary>
    member _.AcceptAsync
        (
            sessionId: SessionId,
            tool: string,
            run: CancellationToken -> Task<Result<JsonElement, ARCPError>>,
            ?origin: Job.JobOrigin,
            ?correlationId: MessageId
        ) : Task<JobId> =
        let origin = defaultArg origin Job.InProcess

        task {
            let jid = JobId.create ()
            let cts = new CancellationTokenSource()

            let job: Job.Job =
                {
                    JobId = jid
                    SessionId = sessionId
                    Tool = tool
                    State = Job.Accepted
                    StartedAt = None
                    LastHeartbeatAt = None
                    HeartbeatSequence = 0
                    CancellationSource = cts
                    Cts = cts
                    Origin = origin
                    HeartbeatChannel = Channel.CreateUnbounded<unit>()
                    CorrelationId = correlationId
                }

            jobs.[jid] <- job

            let _ = Task.Run(fun () -> runJob job run)
            return jid
        }

    /// <summary>
    /// Register an externally-driven heartbeat for the job. Resets the watchdog.
    /// </summary>
    member _.RecordHeartbeatAsync(jobId: JobId, sequence: int, _deadlineMs: int) : Task<unit> =
        task {
            match jobs.TryGetValue jobId with
            | true, job ->
                job.HeartbeatSequence <- sequence
                job.LastHeartbeatAt <- Some(timeProvider.GetUtcNow())
                let! _ = job.HeartbeatChannel.Writer.WriteAsync(()).AsTask()
                return ()
            | _ -> return ()
        }

    /// <summary>
    /// Cooperative cancel. Emits <c>cancel.accepted</c> if the job is
    /// cancellable; signals the CTS; escalates to <c>job.cancelled</c>
    /// (<c>ABORTED</c>) if the runner does not terminate within deadline.
    /// </summary>
    member _.CancelAsync(jobId: JobId, reason: string option, deadlineMs: int option) : Task<Result<unit, ARCPError>> =
        task {
            match jobs.TryGetValue jobId with
            | false, _ -> return Error(NotFound(sprintf "job %A" jobId))
            | true, job ->
                if Job.isTerminal job.State then
                    let env =
                        Envelopes.cancelRefused
                            {
                                Target = "job"
                                TargetId = JobId.value jobId
                                Reason = Some "already terminal"
                            }
                        |> Envelope.withSession job.SessionId
                        |> Envelope.withJob jobId

                    do! emit env
                    return Error(FailedPrecondition "job already terminal")
                else
                    let env =
                        Envelopes.cancelAccepted
                            {
                                Target = "job"
                                TargetId = JobId.value jobId
                            }
                        |> Envelope.withSession job.SessionId
                        |> Envelope.withJob jobId

                    do! emit env
                    job.Cts.Cancel()

                    let deadline =
                        deadlineMs
                        |> Option.map (fun ms -> TimeSpan.FromMilliseconds(float ms))
                        |> Option.defaultValue (TimeSpan.FromSeconds 2.0)

                    let startedAt = timeProvider.GetUtcNow()
                    let mutable observedTerminal = false

                    while not observedTerminal && (timeProvider.GetUtcNow() - startedAt) < deadline do
                        if Job.isTerminal job.State then
                            observedTerminal <- true
                        else
                            do! Task.Delay(TimeSpan.FromMilliseconds 10.0, timeProvider)

                    if not (Job.isTerminal job.State) then
                        let _err = Aborted(sprintf "job %A did not cancel in deadline" jobId)
                        job.State <- Job.Cancelled(Some "aborted: deadline exceeded")

                        let env =
                            Envelopes.jobCancelled
                                {
                                    Reason = Some "aborted: deadline exceeded"
                                }
                            |> Envelope.withSession job.SessionId
                            |> Envelope.withJob jobId
                            |> withCorr job.CorrelationId

                        do! emit env
                        return Ok()
                    else
                        return Ok()
        }

    /// <summary>
    /// Transition a running job to <see cref="Blocked"/> and emit a
    /// <c>human.input.request</c> envelope carrying <paramref name="prompt"/>.
    /// </summary>
    member _.InterruptAsync(jobId: JobId, prompt: string) : Task<Result<unit, ARCPError>> =
        task {
            match jobs.TryGetValue jobId with
            | false, _ -> return Error(NotFound(sprintf "job %A" jobId))
            | true, job ->
                if Job.isTerminal job.State then
                    return Error(FailedPrecondition "job already terminal")
                else
                    job.State <- Job.Blocked "interrupted: awaiting human input"

                    let payload: HumanInputRequest =
                        {
                            Prompt = prompt
                            ResponseSchema = None
                            Default = None
                            ExpiresAt = timeProvider.GetUtcNow().AddMinutes 5.0
                        }

                    let env =
                        Envelopes.humanInputRequest payload
                        |> Envelope.withSession job.SessionId
                        |> Envelope.withJob jobId

                    do! emit env
                    return Ok()
        }

    /// <summary>
    /// Emit a <c>job.progress</c> envelope from an in-process runner.
    /// </summary>
    member _.ProgressAsync(jobId: JobId, percent: int option, message: string option) : Task<unit> =
        task {
            match jobs.TryGetValue jobId with
            | false, _ -> return ()
            | true, job ->
                let env =
                    Envelopes.jobProgress { Percent = percent; Message = message }
                    |> Envelope.withSession job.SessionId
                    |> Envelope.withJob jobId

                do! emit env
                return ()
        }

    /// <summary>Snapshot of the job's current state, or <c>None</c> if unknown.</summary>
    member _.TryGetState(jobId: JobId) : Job.JobState option =
        match jobs.TryGetValue jobId with
        | true, job -> Some job.State
        | _ -> None

    /// <summary>Number of registered jobs (terminal or not).</summary>
    member _.Count: int = jobs.Count

    interface IDisposable with
        member _.Dispose() =
            // Cancel every CTS so heartbeat/watchdog loops exit promptly.
            // We intentionally do NOT dispose the CTS here — outstanding
            // job tasks may still observe the token on their way to terminal,
            // and `CancellationTokenSource.Dispose` would throw inside their
            // continuations.
            for kv in jobs do
                let job = kv.Value

                try
                    if not job.Cts.IsCancellationRequested then
                        job.Cts.Cancel()
                with _ ->
                    ()
