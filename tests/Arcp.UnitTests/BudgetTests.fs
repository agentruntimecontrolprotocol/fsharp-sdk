module ARCP.UnitTests.BudgetTests

open Xunit
open FsUnit.Xunit
open ARCP.Runtime.Internal

[<Fact>]
let ``decimal arithmetic does not round`` () =
    let counters = BudgetCounters()
    counters.SetInitial(Map.ofList [ "USD", 1.00m ])
    counters.TryDecrement("USD", 0.1m) |> ignore
    counters.TryDecrement("USD", 0.1m) |> ignore
    counters.TryDecrement("USD", 0.1m) |> ignore
    counters.Remaining "USD" |> should equal (Some 0.70m)

[<Fact>]
let ``negative decrement is ignored`` () =
    let counters = BudgetCounters()
    counters.SetInitial(Map.ofList [ "USD", 1.00m ])
    counters.TryDecrement("USD", -1m) |> ignore
    counters.Remaining "USD" |> should equal (Some 1.00m)

[<Fact>]
let ``decrement crosses zero into negative`` () =
    let counters = BudgetCounters()
    counters.SetInitial(Map.ofList [ "USD", 0.50m ])
    counters.TryDecrement("USD", 1.00m) |> ignore
    let r = counters.Remaining "USD"
    r |> should equal (Some -0.50m)

[<Fact>]
let ``not-budgeted currency returns None on decrement`` () =
    let counters = BudgetCounters()
    counters.SetInitial(Map.ofList [ "USD", 1m ])
    counters.TryDecrement("EUR", 0.5m) |> should equal None
