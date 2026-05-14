module ArcpSamples.ListJobs

// Demonstrates `list_jobs` (§6.6): the client requests an inventory
// of jobs visible to its principal.

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
                    (fun s -> s.RegisterAgent("alpha", echoAgent))
                    (Set.ofList [ Features.ListJobs ])
            // Submit two jobs.
            for _ in 1 .. 2 do
                let! _ = p.Client.SubmitAsync(
                            { Agent = "alpha"; Input = jsonInt 0
                              LeaseRequest = None; LeaseConstraints = None
                              IdempotencyKey = None; MaxRuntimeSec = None },
                            CancellationToken.None)
                ()
            let! response = p.Client.ListJobsAsync(None, Some 10, None, CancellationToken.None)
            writeLine (sprintf "found %d jobs" response.Jobs.Length)
            for j in response.Jobs do
                writeLine (sprintf "  %s — %s (%s)" j.JobId j.Agent (JobStatus.toWire j.Status))
            do! teardown p
            return 0
        })
