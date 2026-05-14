module ArcpSamples.QuickStart

// <!-- region quickstart -->
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
                        server.RegisterAgent("hello", fun ctx ->
                            task {
                                do! ctx.EmitLogAsync(LogLevel.Info, "saying hello", ctx.CancellationToken)
                                return jsonString "Hello, ARCP!"
                            }))
                    Features.All

            let! handle =
                p.Client.SubmitAsync(
                    { Agent = "hello"
                      Input = jsonInt 0
                      LeaseRequest = None
                      LeaseConstraints = None
                      IdempotencyKey = None
                      MaxRuntimeSec = None },
                    CancellationToken.None)

            let! result = handle.Result
            match result with
            | Ok r ->
                writeLine (r.Result |> Option.map (fun v -> v.GetRawText()) |> Option.defaultValue "null")
            | Error e ->
                writeErr (sprintf "job failed: %s" (ARCPError.code e))

            do! teardown p
            return 0
        })
// <!-- endregion -->
