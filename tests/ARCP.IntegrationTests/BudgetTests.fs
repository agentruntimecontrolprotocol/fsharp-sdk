module ARCP.IntegrationTests.BudgetTests

open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime
open ARCP.IntegrationTests.Harness

[<Fact>]
let ``cost.budget decrements on metric and rejects with BUDGET_EXHAUSTED`` () =
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
                            do! ctx.EmitMetricAsync("cost.search", 0.5m, Some "USD", None, ctx.CancellationToken)
                            do! ctx.EmitMetricAsync("cost.search", 0.6m, Some "USD", None, ctx.CancellationToken)
                            do! Task.Delay(50)  // let metrics flush
                            do! ctx.ValidateOpAsync(Capabilities.ToolCall, "search.web", ctx.CancellationToken)
                            return Json.serializeToElement<int> 0
                        }))
                (Set.singleton Features.CostBudget)
        let req =
            { mkRequest "research" with LeaseRequest = Some lease }
        let! handle = p.Client.SubmitAsync(req, CancellationToken.None)
        let! r = handle.Result
        match r with
        | Error (ARCPError.BudgetExhausted "USD") -> ()
        | other -> failwithf "expected BudgetExhausted USD, got %A" other
        do! teardown p
    }
