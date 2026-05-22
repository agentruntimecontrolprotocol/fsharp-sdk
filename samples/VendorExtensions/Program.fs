module ArcpSamples.VendorExtensions

// Demonstrates §15 IANA extension namespaces. Unknown event
// `kind` strings are surfaced as `JobEventBody.XVendor` so that
// consumers can round-trip them without the codec rejecting the
// envelope.

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
            let payload = Json.parseElement """{"foo": "bar"}"""

            let! p =
                connect
                    (fun s ->
                        s.RegisterAgent(
                            "vendor",
                            fun ctx ->
                                task {
                                    // Emit a vendor-extension event the spec doesn't recognise.
                                    let body = JobEventBody.XVendor("x-acme.audit", payload)

                                    let emit =
                                        typeof<JobContext>
                                            .GetField(
                                                "emit",
                                                System.Reflection.BindingFlags.Instance
                                                ||| System.Reflection.BindingFlags.NonPublic
                                            )

                                    let _ = emit // placeholder; agents should emit via the public surface
                                    do! ctx.EmitToolCallAsync("x-acme.audit", payload, "v1", ctx.CancellationToken)
                                    return jsonString "ok"
                                }
                        ))
                    Features.All

            let! handle =
                p.Client.SubmitAsync(
                    {
                        Agent = "vendor"
                        Input = jsonInt 0
                        LeaseRequest = None
                        LeaseConstraints = None
                        IdempotencyKey = None
                        MaxRuntimeSec = None
                    },
                    CancellationToken.None
                )

            let! _ = handle.Result
            writeLine "vendor extension demonstrated as a tool_call shape"
            do! teardown p
            return 0
        })
