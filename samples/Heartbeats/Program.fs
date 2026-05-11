/// Supervisor + worker pool. Heartbeat loss reroutes via idempotency_key.
module ARCP.Samples.Heartbeats.Program

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open ARCP.Client
open ARCP.Envelope
open ARCP.Ids
open ARCP.Samples.Heartbeats.Work

let heartbeatIntervalSec = 15
let deadlineSec = heartbeatIntervalSec * 2 // RFC §10.3 default N=2

type Worker =
    {
        WorkerId: string
        Role: string
        mutable LastHeartbeat: DateTimeOffset
        mutable InFlightJob: JobId option
    }

type WorkTask =
    {
        TaskId: string
        Role: string
        Payload: JsonElement
        IdempotencyKey: IdempotencyKey
    }

type Roster() =
    let workers = ConcurrentDictionary<string, Worker>()
    let byRole = ConcurrentDictionary<string, List<string>>()

    member _.Add(w: Worker) =
        workers.[w.WorkerId] <- w
        let lst = byRole.GetOrAdd(w.Role, fun _ -> List())
        lst.Add w.WorkerId

    member _.Candidates(role: string) : Worker list =
        match byRole.TryGetValue role with
        | true, lst ->
            [
                for wid in lst do
                    match workers.TryGetValue wid with
                    | true, w when w.InFlightJob.IsNone -> yield w
                    | _ -> ()
            ]
        | _ -> []

    member _.Workers = workers
    member _.ByRole = byRole

// Supervisor side --------------------------------------------------------

/// Same idempotency_key on every re-dispatch (RFC §6.4): a worker that
/// survived the network blip dedupes — it doesn't re-execute.
let dispatch
    (client: Client)
    (task: WorkTask)
    (roster: Roster)
    (jobsToTasks: ConcurrentDictionary<JobId, WorkTask>)
    : Task =
    task {
        let candidates = roster.Candidates task.Role

        if List.isEmpty candidates then
            failwithf "no idle workers for role=%s" task.Role

        let worker = candidates |> List.minBy (fun w -> w.LastHeartbeat)

        // Real call: client.DelegateAsync(worker.WorkerId, task = task.TaskId,
        //                                 idempotencyKey = task.IdempotencyKey, ...)
        let jobId: JobId = JobId.create ()
        worker.InFlightJob <- Some jobId
        jobsToTasks.[jobId] <- task
        return ()
    }
    :> Task

let supervise (client: Client) (roster: Roster) (jobsToTasks: ConcurrentDictionary<JobId, WorkTask>) : Task =
    task {
        let reaper () =
            task {
                while true do
                    do! Task.Delay(TimeSpan.FromSeconds(float heartbeatIntervalSec))
                    let now = DateTimeOffset.UtcNow

                    for w in roster.Workers.Values |> Seq.toList do
                        if (now - w.LastHeartbeat).TotalSeconds > float deadlineSec then
                            match w.InFlightJob with
                            | Some jid ->
                                match jobsToTasks.TryRemove jid with
                                | true, t -> do! dispatch client t roster jobsToTasks
                                | _ -> ()
                            | None -> ()

                            roster.Workers.TryRemove w.WorkerId |> ignore
            }
            :> Task

        let _ = Task.Run(reaper)

        // Drain inbound:
        // for env in client.Events do
        //     match env.Type, env.JobId with
        //     | "job.heartbeat", Some jid ->
        //         for w in roster.Workers.Values do
        //             if w.InFlightJob = Some jid then w.LastHeartbeat <- DateTimeOffset.UtcNow
        //     | ("job.completed" | "job.failed" | "job.cancelled"), Some jid ->
        //         jobsToTasks.TryRemove jid |> ignore
        //         for w in roster.Workers.Values do
        //             if w.InFlightJob = Some jid then w.InFlightJob <- None
        //     | _ -> ()
        return failwith "elided: drain client.Events"
    }
    :> Task

// Worker side ------------------------------------------------------------

let heartbeatLoop (client: Client) (jobId: JobId) (stop: CancellationToken) : Task =
    task {
        let mutable seq = 0

        while not stop.IsCancellationRequested do
            // Real call: client.SendHeartbeatAsync(jobId, sequence = seq, deadlineMs = ..., state = "running")
            seq <- seq + 1

            try
                do! Task.Delay(TimeSpan.FromSeconds(float heartbeatIntervalSec), stop)
            with _ ->
                ()
    }
    :> Task

let execute (client: Client) (env: Envelope<JsonElement>) : Task =
    task {
        let jobId = JobId.create ()
        // client.SendJobAcceptedAsync(jobId, correlationId = env.Id)
        // client.SendJobStartedAsync(jobId)
        use stop = new CancellationTokenSource()
        let hb = heartbeatLoop client jobId stop.Token

        try
            try
                let payload = env.Payload.GetProperty("context").GetProperty("task_payload")
                let! result = doWork payload
                // client.SendJobCompletedAsync(jobId, result)
                ()
            with ex ->
                // client.SendJobFailedAsync(jobId, code = "INTERNAL", message = ex.Message, retryable = true)
                ()
        finally
            stop.Cancel()
    }
    :> Task

[<EntryPoint>]
let main _ =
    task {
        let supervisor: Client = Unchecked.defaultof<_> // transport, identity (privileged), auth elided
        let roster = Roster()
        let jobsToTasks = ConcurrentDictionary<JobId, WorkTask>()

        for role in [ "indexer"; "extractor"; "archiver" ] do
            for _ in 1..2 do
                roster.Add
                    {
                        WorkerId = sprintf "%s-%s" role (Guid.NewGuid().ToString().Substring(0, 6))
                        Role = role
                        LastHeartbeat = DateTimeOffset.UtcNow
                        InFlightJob = None
                    }

        let _ = supervise supervisor roster jobsToTasks

        for n in 0..5 do
            let role = [ "indexer"; "extractor"; "archiver" ].[n % 3]

            do!
                dispatch
                    supervisor
                    {
                        TaskId = sprintf "t%03d" n
                        Role = role
                        Payload = JsonDocument.Parse(sprintf "{\"shard\":%d}" n).RootElement
                        IdempotencyKey = IdempotencyKey.create (sprintf "openclaw:t%03d" n)
                    }
                    roster
                    jobsToTasks

        do! Task.Delay(TimeSpan.FromSeconds 60.0)
        return ()
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
