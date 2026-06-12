module ARCP.UnitTests.PendingRegistryTests

open System
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Client.Internal

let private env id =
    Envelope.create "session.welcome" (Json.serializeToElement<int> 0)
    |> Envelope.withId id

[<Fact>]
let ``Register then TryComplete resolves the awaiting task`` () =
    let reg = PendingRegistry()
    let task = reg.Register "req-1"
    task.IsCompleted |> should equal false
    let completed = reg.TryComplete("req-1", env "req-1")
    completed |> should equal true
    task.Wait()
    task.Result.Id |> should equal "req-1"

[<Fact>]
let ``TryComplete returns false for unknown ids`` () =
    let reg = PendingRegistry()
    reg.TryComplete("missing", env "missing") |> should equal false

[<Fact>]
let ``Duplicate Register throws`` () =
    let reg = PendingRegistry()
    reg.Register "req-1" |> ignore

    Assert.Throws<ARCP.Core.ArcpException>(fun () -> reg.Register "req-1" |> ignore)
    |> ignore

[<Fact>]
let ``FailAll surfaces the exception on all pending tasks`` () =
    let reg = PendingRegistry()
    let t1 = reg.Register "req-1"
    let t2 = reg.Register "req-2"
    reg.FailAll(InvalidOperationException "boom")

    let aw1 = Assert.Throws<AggregateException>(fun () -> t1.Wait())

    let aw2 = Assert.Throws<AggregateException>(fun () -> t2.Wait())

    aw1.InnerException.Message |> should equal "boom"
    aw2.InnerException.Message |> should equal "boom"

[<Fact>]
let ``TryComplete after a request was consumed returns false`` () =
    let reg = PendingRegistry()
    reg.Register "req-1" |> ignore
    reg.TryComplete("req-1", env "req-1") |> should equal true
    reg.TryComplete("req-1", env "req-1") |> should equal false
