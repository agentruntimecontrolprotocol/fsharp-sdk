module ARCP.IntegrationTests.ListJobsTests

open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime
open ARCP.IntegrationTests.Harness

[<Fact>]
let ``list_jobs returns submitted jobs`` () =
    task {
        let! p =
            connect
                (fun s -> s.RegisterAgent("a", fun _ -> task { return Json.serializeToElement<int> 0 }))
                (Set.singleton Features.ListJobs)
        let! _ = p.Client.SubmitAsync(mkRequest "a", CancellationToken.None)
        let! _ = p.Client.SubmitAsync(mkRequest "a", CancellationToken.None)
        let! response = p.Client.ListJobsAsync(None, None, None, CancellationToken.None)
        response.Jobs.Length |> should be (greaterThanOrEqualTo 2)
        do! teardown p
    }

[<Fact>]
let ``list_jobs without negotiation throws`` () =
    task {
        let! p = connect (fun s -> s.RegisterAgent("a", fun _ -> task { return Json.serializeToElement<int> 0 })) Set.empty
        let! threw =
            task {
                try
                    let! _ = p.Client.ListJobsAsync(None, None, None, CancellationToken.None)
                    return false
                with :? ArcpException -> return true
            }
        threw |> should equal true
        do! teardown p
    }
