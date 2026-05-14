module ArcpSamples.SubmitAndStream

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
                    (fun server ->
                        server.RegisterAgent("worker", fun ctx ->
                            task {
                                do! ctx.EmitStatusAsync("running", Some "starting", ctx.CancellationToken)
                                for i in 1 .. 3 do
                                    do! ctx.EmitLogAsync(LogLevel.Info, sprintf "step %d" i, ctx.CancellationToken)
                                do! ctx.EmitStatusAsync("running", Some "done", ctx.CancellationToken)
                                return jsonString "completed"
                            }))
                    Features.All

            let! handle =
                p.Client.SubmitAsync(
                    { Agent = "worker"
                      Input = jsonInt 0
                      LeaseRequest = None
                      LeaseConstraints = None
                      IdempotencyKey = None
                      MaxRuntimeSec = None },
                    CancellationToken.None)

            // Stream events as they arrive.
            let enumerator = handle.Events.GetAsyncEnumerator(CancellationToken.None)
            try
                let mutable more = true
                while more do
                    let! has = enumerator.MoveNextAsync().AsTask()
                    if not has then more <- false
                    else writeLine (sprintf "event: %s" (JobEventBody.kind enumerator.Current))
            finally
                ignore (enumerator.DisposeAsync().AsTask())

            let! result = handle.Result
            match result with
            | Ok r ->
                writeLine (sprintf "result: %s" (r.Result |> Option.map (fun v -> v.GetRawText()) |> Option.defaultValue "null"))
            | Error e ->
                writeErr (sprintf "failed: %s" (ARCPError.code e))

            do! teardown p
            return 0
        })
