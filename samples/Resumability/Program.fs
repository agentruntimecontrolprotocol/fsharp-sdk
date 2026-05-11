/// Durable research job with real crash and resume.
///
/// First call: crash after `synthesize`. Prints the resume token.
///   CRASH_AFTER_STEP=synthesize  dotnet run --project samples/Resumability
///
/// Second call: pick up from the printed checkpoint.
///   RESUME_JOB_ID=...  RESUME_AFTER_MSG_ID=...  RESUME_CHECKPOINT_ID=...
module ARCP.Samples.Resumability.Program

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open FSharp.Control
open ARCP.Client
open ARCP.Errors
open ARCP.Ids
open ARCP.Samples.Resumability.Steps

let steps = [| "plan"; "gather"; "synthesize"; "critique"; "finalize" |]

/// Deterministic per-step idempotency key (RFC §6.4). Re-issuing the same
/// step with the same input returns the prior outcome instead of re-running.
let stepKey (jobId: JobId) (step: string) (salt: string) : IdempotencyKey =
    use sha = SHA256.Create()

    let bytes =
        sha.ComputeHash(Encoding.UTF8.GetBytes(sprintf "%s\x00%s\x00%s" (JobId.value jobId) step salt))

    let digest =
        bytes
        |> Array.take 8
        |> Array.map (fun b -> b.ToString "x2")
        |> String.concat ""

    IdempotencyKey.create (sprintf "research:%s:%s:%s" (JobId.value jobId) step digest)

let emitProgress (client: Client) (jobId: JobId) (step: string) : Task =
    task {
        let pct = 100.0 * float (Array.findIndex ((=) step) steps + 1) / float steps.Length
        // client.SendJobProgressAsync(jobId, percent = pct, message = step)
        return ()
    }
    :> Task

let emitCheckpoint (client: Client) (jobId: JobId) (step: string) : Task<string> =
    task {
        let chk =
            sprintf "chk_%s_%s" step ((JobId.value jobId) |> fun s -> s.Substring(s.Length - 6))
        // client.SendJobCheckpointAsync(jobId, checkpointId = chk, label = step)
        return chk
    }

let executeSteps
    (client: Client)
    (jobId: JobId)
    (request: obj)
    (startingAt: string)
    (crashAfter: string option)
    : Task<obj> =
    task {
        let mutable output = request
        let startIdx = Array.findIndex ((=) startingAt) steps

        for i in startIdx .. steps.Length - 1 do
            let step = steps.[i]
            let _key = stepKey jobId step (string output)
            do! emitProgress client jobId step
            let! out = runStep client jobId step (Map.ofList [ "prior", output ])
            output <- out
            let! _ = emitCheckpoint client jobId step

            if crashAfter = Some step then
                printfn
                    "[crash after %s; resume with RESUME_JOB_ID=%s RESUME_CHECKPOINT_ID=chk_%s_<…>]"
                    step
                    (JobId.value jobId)
                    step
                // The whole point of durable jobs: process death is fine.
                // Runtime kept every envelope; resume picks it up.
                Environment.Exit 137

        return output
    }

/// Replay envelopes; return the last checkpoint label, or None if the job
/// already terminated during replay.
let issueResume
    (client: Client)
    (sessionId: SessionId)
    (jobId: JobId)
    (afterMessageId: MessageId)
    (checkpointId: string option)
    : Task<string option> =
    task {
        match! client.ResumeAsync(sessionId, afterMessageId) with
        | Error(DataLoss _) -> return failwith "retention expired"
        | Error e -> return failwithf "resume failed: %A" e
        | Ok() ->
            // Drain the replayed envelopes through the receive loop and pick
            // off the last checkpoint label.
            //   for env in client.Events do
            //     if env.JobId = Some jobId then
            //       match env.Type with
            //       | "job.checkpoint" -> last <- Some (env.Payload.GetProperty("label").GetString())
            //       | "job.completed" | "job.failed" | "job.cancelled" -> return None
            //       | "event.emit" when name = "subscription.backfill_complete" -> return last
            return failwith "elided: drain replay envelopes"
    }

[<EntryPoint>]
let main _ =
    task {
        let client: Client = Unchecked.defaultof<_> // transport, identity, auth elided

        match
            Environment.GetEnvironmentVariable "RESUME_JOB_ID", Environment.GetEnvironmentVariable "RESUME_AFTER_MSG_ID"
        with
        | (rjId, rjAfter) when not (isNull rjId) && not (isNull rjAfter) ->
            let sessionId = SessionId.ofString "..."

            let! last =
                issueResume
                    client
                    sessionId
                    (JobId.ofString rjId)
                    (MessageId.ofString rjAfter)
                    (Option.ofObj (Environment.GetEnvironmentVariable "RESUME_CHECKPOINT_ID"))

            match last with
            | None -> printfn "already terminal during replay"
            | Some lbl ->
                let nextIdx = Array.findIndex ((=) lbl) steps + 1

                if nextIdx >= steps.Length then
                    printfn "nothing to resume"
                else
                    printfn "[resuming at %s]" steps.[nextIdx]

                    let! _final = executeSteps client (JobId.ofString rjId) "<replayed>" steps.[nextIdx] None

                    ()
        | _ ->
            let jobId = JobId.create ()
            let request = "Survey CRDT-based collaborative editing in 2026."

            let! final =
                executeSteps
                    client
                    jobId
                    request
                    steps.[0]
                    (Option.ofObj (Environment.GetEnvironmentVariable "CRASH_AFTER_STEP"))

            printfn "job_id=%s\n%A" (JobId.value jobId) final

        return ()
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
