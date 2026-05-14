namespace ARCP.Runtime.Internal

open System.Collections.Concurrent
open ARCP.Core

/// Per-currency budget counters (spec §9.6).
///
/// Decimal arithmetic — never float. Cost metrics decrement;
/// the lease validator reads the snapshot to check ≤ 0.
type BudgetCounters() =
    let counters = ConcurrentDictionary<string, decimal ref>()

    /// Initialise counters from a lease's `cost.budget` entry.
    member _.SetInitial(initial: Map<string, decimal>) : unit =
        for kvp in initial do
            counters.[kvp.Key] <- ref kvp.Value

    /// Decrement `currency` by `amount`. Negative or zero amounts
    /// are ignored. Returns the new remaining value, or `None` if
    /// the currency is not budgeted (no enforcement applies).
    member _.TryDecrement(currency: string, amount: decimal) : decimal option =
        if amount <= 0m then
            match counters.TryGetValue currency with
            | true, r -> Some r.Value
            | _ -> None
        else
            match counters.TryGetValue currency with
            | true, r ->
                lock r (fun () ->
                    r.Value <- r.Value - amount
                    Some r.Value)
            | _ -> None

    /// Snapshot all counters.
    member _.Snapshot() : Map<string, decimal> =
        counters
        |> Seq.map (fun kvp -> kvp.Key, kvp.Value.Value)
        |> Map.ofSeq

    member _.IsBudgeted(currency: string) : bool =
        counters.ContainsKey currency

    member _.Remaining(currency: string) : decimal option =
        match counters.TryGetValue currency with
        | true, r -> Some r.Value
        | _ -> None
