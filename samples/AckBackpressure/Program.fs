module ArcpSamples.AckBackpressure

// Demonstrates `ack` (§6.5): the client emits `session.ack` every
// 32 events / 250 ms automatically once the feature is negotiated.

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
                        s.RegisterAgent("chatty", fun ctx ->
                            task {
                                for i in 1 .. 64 do
                                    do! ctx.EmitLogAsync(LogLevel.Info, sprintf "msg %d" i, ctx.CancellationToken)
                                return jsonString "done"
                            }))
                    (Set.ofList [ Features.Ack ])

            let! handle = p.Client.SubmitAsync(
                { Agent = "chatty"
                  Input = jsonInt 0
                  LeaseRequest = None
                  LeaseConstraints = None
                  IdempotencyKey = None
                  MaxRuntimeSec = None }, CancellationToken.None)
            let! _ = handle.Result
            writeLine "done — auto-ack flushed via 32-event window"
            do! teardown p
            return 0
        })
