module ARCP.IntegrationTests.IdempotencyAndCancelTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime
open ARCP.IntegrationTests.Harness

[<Fact>]
let ``parallel duplicate idempotency-key submits never launch two jobs`` () =
    task {
        let invocations = ref 0

        let! p =
            connect
                (fun s ->
                    s.RegisterAgent(
                        "ok",
                        fun _ ->
                            Interlocked.Increment(invocations) |> ignore
                            task { return Json.serializeToElement<int> 0 }
                    ))
                Features.All

        let req =
            { mkRequest "ok" with
                IdempotencyKey = Some "race-key"
            }

        let t1 = p.Client.SubmitAsync(req, CancellationToken.None)
        let t2 = p.Client.SubmitAsync(req, CancellationToken.None)
        let! h1 = t1
        let! h2 = t2

        // Idempotency contract: both calls return the same JobId.
        h1.JobId.Value |> should equal h2.JobId.Value
        do! Task.Delay 100
        // Exactly one job ran.
        invocations.Value |> should equal 1
        do! teardown p
    }

[<Fact>]
let ``list_jobs filter by agent narrows the set`` () =
    task {
        let! p =
            connect
                (fun s ->
                    s.RegisterAgent("a", fun _ -> task { return Json.serializeToElement<int> 0 })
                    s.RegisterAgent("b", fun _ -> task { return Json.serializeToElement<int> 0 }))
                (Set.singleton Features.ListJobs)

        let! _ = p.Client.SubmitAsync(mkRequest "a", CancellationToken.None)
        let! _ = p.Client.SubmitAsync(mkRequest "b", CancellationToken.None)
        let! _ = p.Client.SubmitAsync(mkRequest "b", CancellationToken.None)

        let filter: JobListFilter =
            {
                Status = None
                Agent = Some "a@default"
                CreatedAfter = None
            }

        let! response = p.Client.ListJobsAsync(Some filter, None, None, CancellationToken.None)
        response.Jobs.Length |> should equal 1
        do! teardown p
    }

[<Fact>]
let ``list_jobs limit caps the result count`` () =
    task {
        let! p =
            connect
                (fun s -> s.RegisterAgent("a", fun _ -> task { return Json.serializeToElement<int> 0 }))
                (Set.singleton Features.ListJobs)

        for _ in 1..3 do
            let! _ = p.Client.SubmitAsync(mkRequest "a", CancellationToken.None)
            ()

        let! response = p.Client.ListJobsAsync(None, Some 1, None, CancellationToken.None)
        response.Jobs.Length |> should equal 1
        do! teardown p
    }
