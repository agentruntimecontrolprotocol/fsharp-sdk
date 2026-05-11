/// Two scenarios over the §10.4 / §10.5 control surface.
module ARCP.Samples.Cancellation.Program

open System
open System.Text.Json
open System.Threading.Tasks
open FSharp.Control
open ARCP.Client
open ARCP.Errors
open ARCP.Envelope
open ARCP.Ids

let cancelDeadlineMs = 5_000

let startLongJob (client: Client) : Task<JobId> =
    task {
        let args = JsonDocument.Parse("""{"work_seconds":600}""").RootElement

        let! jid, _ = client.InvokeWithJobIdAsync("demo.long_running", args)
        return jid
    }

/// Cooperative cancel. Runtime drives target to a clean checkpoint inside
/// `deadlineMs` before terminating; escalates to ABORTED on timeout (RFC §10.4).
let cancelJob (client: Client) (jobId: JobId) (reason: string) (deadlineMs: int) : Task<Result<unit, ARCPError>> =
    client.CancelAsync(jobId, reason = reason, deadlineMs = deadlineMs)

/// Distinct from cancel: pauses the job (`blocked`), runtime emits
/// `human.input.request`. Job is NOT terminated (RFC §10.5).
let interruptJob (client: Client) (jobId: JobId) (prompt: string) : Task =
    task {
        // client.SendInterruptAsync(target = "job", targetId = jobId, prompt = prompt)
        return failwith "elided: interrupt envelope"
    }
    :> Task

let scenarioCancel () : Task =
    task {
        let client: Client = Unchecked.defaultof<_> // transport, identity, auth elided

        try
            let! jobId = startLongJob client
            do! Task.Delay(TimeSpan.FromSeconds 2.0) // let the job actually start

            match! cancelJob client jobId "user_aborted" cancelDeadlineMs with
            | Ok() -> printfn "cancel ack"
            | Error e -> printfn "cancel failed: %s" (ARCPError.message e)

            // Drain to terminal:
            // for env in client.Events do
            //     match env.Type with
            //     | "job.completed" | "job.failed" | "job.cancelled" when env.JobId = Some jobId ->
            //         printfn "terminal: %s" env.Type
            //         return
            //     | _ -> ()
            return ()
        finally
            ()
    }
    :> Task

let scenarioInterrupt () : Task =
    task {
        let client: Client = Unchecked.defaultof<_>

        try
            let! jobId = startLongJob client
            do! Task.Delay(TimeSpan.FromSeconds 2.0)
            do! interruptJob client jobId "Pause and ask before touching production tables."

            // Runtime now emits human.input.request; answer via the HumanInput sample.
            // for env in client.Events do
            //     if env.Type = "human.input.request" && env.JobId = Some jobId then
            //         printfn "awaiting human: %s" (env.Payload.GetProperty("prompt").GetString())
            //         return
            return ()
        finally
            ()
    }
    :> Task

[<EntryPoint>]
let main argv =
    let which = if argv.Length > 0 then argv.[0] else "cancel"

    let t =
        match which with
        | "cancel" -> scenarioCancel ()
        | "interrupt" -> scenarioInterrupt ()
        | other -> failwithf "unknown scenario: %s" other

    t.GetAwaiter().GetResult()
    0
