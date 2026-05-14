module ArcpSamples.IdempotentRetry

// Demonstrates `idempotency_key` (§7.2). Submitting the same
// `job.submit` twice with the same key returns the original
// `job.accepted`, not a new job.

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
            let! p = connect (fun s -> s.RegisterAgent("ping", echoAgent)) Features.All
            let req: JobSubmitRequest = {
                Agent = "ping"
                Input = jsonInt 0
                LeaseRequest = None
                LeaseConstraints = None
                IdempotencyKey = Some "the-key"
                MaxRuntimeSec = None
            }
            let! h1 = p.Client.SubmitAsync(req, CancellationToken.None)
            let! h2 = p.Client.SubmitAsync(req, CancellationToken.None)
            writeLine (sprintf "first:  %s" h1.JobId.Value)
            writeLine (sprintf "second: %s" h2.JobId.Value)
            writeLine (sprintf "same? %b" (h1.JobId.Value = h2.JobId.Value))
            do! teardown p
            return 0
        })
