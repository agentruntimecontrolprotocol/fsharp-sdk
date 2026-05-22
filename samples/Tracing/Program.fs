module ArcpSamples.Tracing

// Demonstrates `Arcp.Otel`. Spans tagged with `arcp.session_id`,
// `arcp.job_id`, `arcp.agent`, `arcp.lease.capabilities`, etc.

open System.Threading
open System.Threading.Tasks
open OpenTelemetry
open OpenTelemetry.Trace
open ARCP.Core
open ARCP.Otel
open ARCP.Runtime
open ArcpSamples.SampleHarness

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            use tracer =
                Sdk.CreateTracerProviderBuilder().AddSource(ArcpActivitySource.Name).AddConsoleExporter().Build()

            let! p =
                connect
                    (fun s ->
                        s.RegisterAgent(
                            "traced",
                            fun ctx ->
                                task {
                                    use _ =
                                        ArcpOtel.beginJobSpan
                                            ctx.SessionId
                                            ctx.JobId
                                            "traced@1.0.0"
                                            ctx.Lease
                                            ctx.LeaseConstraints
                                        |> Option.toObj

                                    do! ctx.EmitLogAsync(LogLevel.Info, "hello", ctx.CancellationToken)
                                    return jsonString "ok"
                                }
                        ))
                    Features.All

            let! handle =
                p.Client.SubmitAsync(
                    {
                        Agent = "traced"
                        Input = jsonInt 0
                        LeaseRequest = None
                        LeaseConstraints = None
                        IdempotencyKey = None
                        MaxRuntimeSec = None
                    },
                    CancellationToken.None
                )

            let! _ = handle.Result
            writeLine "tracing demo done"
            do! teardown p
            return 0
        })
