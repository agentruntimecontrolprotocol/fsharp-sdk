module ARCP.IntegrationTests.SubscribeTests

open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Client
open ARCP.Runtime
open ARCP.IntegrationTests.Harness

[<Fact>]
let ``subscribe attaches to a running job`` () =
    task {
        let! p =
            connect
                (fun s ->
                    s.RegisterAgent("slow", fun ctx ->
                        task {
                            do! Task.Delay(50)
                            return Json.serializeToElement<int> 0
                        }))
                (Set.singleton Features.Subscribe)
        let! owner = p.Client.SubmitAsync(mkRequest "slow", CancellationToken.None)
        let! subbed = p.Client.SubscribeAsync(owner.JobId, SubscribeOptions.defaults, CancellationToken.None)
        subbed.JobId.Value |> should equal owner.JobId.Value
        do! teardown p
    }
