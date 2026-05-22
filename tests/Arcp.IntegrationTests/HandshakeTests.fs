module ARCP.IntegrationTests.HandshakeTests

open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime
open ARCP.IntegrationTests.Harness

[<Fact>]
let ``session.hello → session.welcome negotiates feature intersection`` () =
    task {
        let! p =
            connect
                (fun s -> s.RegisterAgent("noop", fun _ -> task { return Json.serializeToElement<int> 0 }))
                Features.All

        let ctx = p.Client.Session.Value

        let expected =
            Features.All
            |> Set.remove Features.ModelUse
            |> Set.remove Features.ProvisionedCredentials

        ctx.NegotiatedFeatures |> should equal expected
        ctx.ResumeToken |> should not' (be NullOrEmptyString)
        do! teardown p
    }

[<Fact>]
let ``welcome.heartbeat_interval_sec is set when heartbeat negotiated`` () =
    task {
        let! p =
            connect
                (fun s -> s.RegisterAgent("noop", fun _ -> task { return Json.serializeToElement<int> 0 }))
                (Set.singleton Features.Heartbeat)

        let ctx = p.Client.Session.Value
        ctx.HeartbeatIntervalSec.IsSome |> should equal true
        do! teardown p
    }
