/// Fan a request out to peer runtimes; tolerate partial failure.
module ARCP.Samples.Delegation.Program

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control
open ARCP.Client
open ARCP.Envelope
open ARCP.Ids
open ARCP.Trace
open ARCP.Samples.Delegation.Synth

let peers = [ "research.web"; "research.code"; "research.docs" ]

let terminalTypes = Set.ofList [ "job.completed"; "job.failed"; "job.cancelled" ]

type Job =
    {
        Target: string
        JobId: JobId option
        Final: JsonElement option
        Error: (string * string) option
    }

/// Issue agent.delegate and await job.accepted (or terminal failure).
let delegateJob (client: Client) (target: string) (task: string) (traceId: TraceId) : Task<Job> =
    task {
        // Real call: client.DelegateAsync(target, task, context = { traceId })
        // returns either a JobId (on job.accepted) or a typed error envelope.
        return failwith "elided: agent.delegate → job.accepted"
    }

/// Single reader on the inbound stream that fans events out by job_id.
/// Without this, parallel `for env in client.Events` loops starve each other.
type JobMux(client: Client) =
    let queues = ConcurrentDictionary<JobId, Channel<Envelope<JsonElement>>>()

    let loopTask =
        task {
            // for env in client.Events do
            //     match env.JobId with
            //     | Some jid when queues.ContainsKey jid ->
            //         let ch = queues.[jid]
            //         ch.Writer.TryWrite env |> ignore
            //         if Set.contains env.Type terminalTypes then ch.Writer.TryComplete() |> ignore
            //     | _ -> ()
            return ()
        }
        :> Task

    member _.Register(jobId: JobId) =
        queues.[jobId] <- Channel.CreateUnbounded<Envelope<JsonElement>>()

    member _.Stream(job: Job) : IAsyncEnumerable<Envelope<JsonElement>> =
        taskSeq {
            match job.JobId with
            | None -> ()
            | Some jid ->
                let ch = queues.[jid]
                let mutable running = true

                while running do
                    let! ok = ch.Reader.WaitToReadAsync()

                    if not ok then
                        running <- false
                    else
                        match ch.Reader.TryRead() with
                        | true, env ->
                            yield env

                            if Set.contains env.Type terminalTypes then
                                running <- false
                        | _ -> ()
        }

let collect (mux: JobMux) (job: Job) : Task<Job> =
    task {
        match job.Error with
        | Some _ -> return job
        | None ->
            let mutable result = job

            do!
                mux.Stream job
                |> TaskSeq.iterAsync (fun env ->
                    task {
                        match env.Type with
                        | "job.completed" -> result <- { result with Final = Some env.Payload }
                        | "job.failed" ->
                            let code = env.Payload.GetProperty("code").GetString()
                            let msg = env.Payload.GetProperty("message").GetString()
                            result <- { result with Error = Some(code, msg) }
                        | "job.cancelled" ->
                            result <-
                                { result with
                                    Error = Some("CANCELLED", "cancelled")
                                }
                        | _ -> ()
                    })

            return result
    }

[<EntryPoint>]
let main _ =
    task {
        let client: Client = Unchecked.defaultof<_> // transport, identity, auth elided
        let mux = JobMux(client)

        let request = "what changed in our auth stack in the last 30 days?"
        let traceId = TraceId.create ()

        let! jobs =
            peers
            |> List.map (fun p -> delegateJob client p request traceId)
            |> Task.WhenAll

        for j in jobs do
            match j.JobId with
            | Some jid -> mux.Register jid
            | None -> ()

        let! completed = jobs |> Array.map (collect mux) |> Task.WhenAll

        printfn "%s" (synthesize request (completed |> Array.toList |> List.map box))
        return ()
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
