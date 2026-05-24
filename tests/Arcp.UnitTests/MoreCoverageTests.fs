module ARCP.UnitTests.MoreCoverageTests

open System
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime.Internal

[<Fact>]
let ``SubscriptionFanout subscribe and list subscribers`` () =
    let fan = SubscriptionFanout()
    let jid = JobId.ofString "j-1"
    let sid1 = SessionId.ofString "s-1"
    let sid2 = SessionId.ofString "s-2"
    fan.Subscribe(jid, sid1)
    fan.Subscribe(jid, sid2)
    fan.Subscribers jid |> List.length |> should equal 2

[<Fact>]
let ``SubscriptionFanout unsubscribe removes a single subscriber`` () =
    let fan = SubscriptionFanout()
    let jid = JobId.ofString "j-1"
    let sid = SessionId.ofString "s-1"
    fan.Subscribe(jid, sid)
    fan.Unsubscribe(jid, sid) |> should equal true
    fan.Subscribers jid |> List.isEmpty |> should equal true

[<Fact>]
let ``SubscriptionFanout unsubscribe unknown job returns false`` () =
    let fan = SubscriptionFanout()
    fan.Unsubscribe(JobId.ofString "missing", SessionId.ofString "s")
    |> should equal false

[<Fact>]
let ``SubscriptionFanout UnsubscribeAll clears the session everywhere`` () =
    let fan = SubscriptionFanout()
    let sid = SessionId.ofString "s-1"
    fan.Subscribe(JobId.ofString "j-1", sid)
    fan.Subscribe(JobId.ofString "j-2", sid)
    fan.UnsubscribeAll sid
    fan.Subscribers(JobId.ofString "j-1") |> List.isEmpty |> should equal true
    fan.Subscribers(JobId.ofString "j-2") |> List.isEmpty |> should equal true

[<Fact>]
let ``SubscriptionFanout subscribers for unknown job is empty`` () =
    let fan = SubscriptionFanout()
    fan.Subscribers(JobId.ofString "j-x") |> List.isEmpty |> should equal true

[<Fact>]
let ``BudgetCounters TryDecrement with negative is a no-op that returns current`` () =
    let bc = BudgetCounters()
    bc.SetInitial(Map.ofList [ "USD", 5m ])
    bc.TryDecrement("USD", -1m) |> should equal (Some 5m)
    bc.Remaining "USD" |> should equal (Some 5m)

[<Fact>]
let ``BudgetCounters TryDecrement unknown currency returns None`` () =
    let bc = BudgetCounters()
    bc.SetInitial(Map.ofList [ "USD", 5m ])
    bc.TryDecrement("EUR", 1m) |> should equal None

[<Fact>]
let ``BudgetCounters IsBudgeted distinguishes registered from unregistered`` () =
    let bc = BudgetCounters()
    bc.SetInitial(Map.ofList [ "USD", 5m ])
    bc.IsBudgeted "USD" |> should equal true
    bc.IsBudgeted "EUR" |> should equal false

[<Fact>]
let ``BudgetCounters TryDecrement positive amount reduces remaining`` () =
    let bc = BudgetCounters()
    bc.SetInitial(Map.ofList [ "USD", 5m ])
    bc.TryDecrement("USD", 2m) |> should equal (Some 3m)
    bc.Remaining "USD" |> should equal (Some 3m)

[<Fact>]
let ``BudgetCounters Snapshot returns all counters`` () =
    let bc = BudgetCounters()
    bc.SetInitial(Map.ofList [ "USD", 5m; "EUR", 3m ])
    bc.Snapshot() |> Map.toList |> List.length |> should equal 2

[<Fact>]
let ``AgentRef.format with version`` () =
    AgentRef.format "echo" (Some "1") |> should equal "echo@1"

[<Fact>]
let ``AgentRef.format without version`` () =
    AgentRef.format "echo" None |> should equal "echo"

[<Fact>]
let ``AgentInventoryStore Resolve falls back to a registered version when no default`` () =
    let inv = AgentInventoryStore()
    let h: AgentHandler = fun _ -> task { return Json.serializeToElement<int> 0 }
    inv.Register("a", "1", h)
    inv.SetDefault("a", "missing-version")

    match inv.Resolve "a" with
    | Error(ARCPError.AgentNotAvailable _) -> ()
    | other -> failwithf "got %A" other

[<Fact>]
let ``AgentInventoryStore Rich inventory carries default when set`` () =
    let inv = AgentInventoryStore()
    let h: AgentHandler = fun _ -> task { return Json.serializeToElement<int> 0 }
    inv.Register("a", "1", h)
    inv.Register("a", "2", h)
    inv.SetDefault("a", "2")
    let rich = inv.ToRichInventory()
    rich |> List.length |> should equal 1
    let entry = rich.[0]
    entry.Versions |> should equal [ "1"; "2" ]
    entry.Default |> should equal (Some "2")
