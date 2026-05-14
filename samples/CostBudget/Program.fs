module ArcpSamples.CostBudget

// Demonstrates `cost.budget` (§9.6). The job's lease declares a
// USD budget; `metric` events with `unit = "USD"` decrement the
// counter; once ≤ 0 any further authority-bearing op fails with
// `BUDGET_EXHAUSTED`.

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
            let lease =
                Lease.empty
                |> Lease.withCapability Capabilities.ToolCall [ "search.*" ]
                |> Lease.withCapability Capabilities.CostBudget [ "USD:1.00" ]
            let! p =
                connect
                    (fun s ->
                        s.RegisterAgent("research", fun ctx ->
                            task {
                                do! ctx.ValidateOpAsync(Capabilities.ToolCall, "search.web", ctx.CancellationToken)
                                do! ctx.EmitMetricAsync("cost.search", 0.42m, Some "USD", None, ctx.CancellationToken)
                                do! ctx.EmitMetricAsync("cost.search", 0.70m, Some "USD", None, ctx.CancellationToken)
                                // Counter now < 0 — next op fails.
                                do! ctx.ValidateOpAsync(Capabilities.ToolCall, "search.web", ctx.CancellationToken)
                                return jsonString "unreachable"
                            }))
                    (Set.ofList [ Features.CostBudget ])
            let! handle = p.Client.SubmitAsync(
                { Agent = "research"; Input = jsonInt 0
                  LeaseRequest = Some lease
                  LeaseConstraints = None
                  IdempotencyKey = None; MaxRuntimeSec = None },
                CancellationToken.None)
            let! r = handle.Result
            match r with
            | Error (ARCPError.BudgetExhausted c) -> writeLine (sprintf "BUDGET_EXHAUSTED on %s" c)
            | Error e -> writeErr (ARCPError.code e)
            | Ok _ -> writeErr "should have failed"
            do! teardown p
            return 0
        })
