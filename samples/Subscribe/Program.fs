module ArcpSamples.Subscribe

// Demonstrates `subscribe` (§7.6): a second client attaches to a
// running job and observes its events without cancel authority.

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
                        s.RegisterAgent("slow", fun ctx ->
                            task {
                                for i in 1 .. 3 do
                                    do! ctx.EmitLogAsync(LogLevel.Info, sprintf "step %d" i, ctx.CancellationToken)
                                    do! Task.Delay(50)
                                return jsonString "done"
                            }))
                    (Set.ofList [ Features.Subscribe ])

            let! handle = p.Client.SubmitAsync(
                { Agent = "slow"; Input = jsonInt 0
                  LeaseRequest = None; LeaseConstraints = None
                  IdempotencyKey = None; MaxRuntimeSec = None },
                CancellationToken.None)
            writeLine (sprintf "submitted %s" handle.JobId.Value)
            let! result = handle.Result
            match result with
            | Ok _ -> writeLine "owner saw completion"
            | Error e -> writeErr (ARCPError.message e)
            do! teardown p
            return 0
        })
