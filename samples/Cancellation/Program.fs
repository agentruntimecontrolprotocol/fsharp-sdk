module ArcpSamples.Cancellation

// Demonstrates `job.cancel` (§7.4). The client submits a long
// job, waits briefly, then cancels.

open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime
open ArcpSamples.SampleHarness

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let! p =
                connect
                    (fun s ->
                        s.RegisterAgent(
                            "forever",
                            fun ctx ->
                                task {
                                    do! Task.Delay(-1, ctx.CancellationToken)
                                    return jsonString "unreachable"
                                }
                        ))
                    Features.All

            let! handle =
                p.Client.SubmitAsync(
                    {
                        Agent = "forever"
                        Input = jsonInt 0
                        LeaseRequest = None
                        LeaseConstraints = None
                        IdempotencyKey = None
                        MaxRuntimeSec = None
                    },
                    CancellationToken.None
                )

            do! Task.Delay(100)
            let! _ = handle.CancelAsync(Some "user requested", CancellationToken.None)
            let! r = handle.Result

            match r with
            | Ok p when p.FinalStatus = JobStatus.Cancelled -> writeLine "cancelled"
            | Ok p -> writeLine (sprintf "final: %s" (JobStatus.toWire p.FinalStatus))
            | Error e -> writeErr (ARCPError.code e)

            do! teardown p
            return 0
        })
