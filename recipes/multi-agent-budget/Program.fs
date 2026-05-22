module ArcpRecipes.MultiAgentBudget

open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime
open ArcpRecipes.RecipeHarness

let private childLease dollars =
    Lease.empty
    |> Lease.withCapability Capabilities.CostBudget [ sprintf "USD:%0.2f" dollars ]
    |> Lease.withCapability Capabilities.ToolCall [ "llm.complete" ]

let private plannerAgent : ArcpAgentHandler =
    fun ctx ->
        task {
            let grants = [ 0.05m; 0.10m; 0.15m; 0.25m ]
            let mutable delegated = 0
            let mutable skipped = 0

            for i, grant in grants |> List.indexed do
                let remaining = ctx.RemainingBudget |> Map.tryFind "USD" |> Option.defaultValue 0m
                if remaining >= grant then
                    let lease = childLease grant
                    let body: DelegateBody = {
                        ChildJobId = (JobId.newId()).Value
                        Agent = "worker"
                        Lease = lease
                        LeaseConstraints = None
                    }
                    do! ctx.EmitDelegateAsync(body, ctx.CancellationToken)
                    do! ctx.EmitMetricAsync("cost.delegate", grant, Some "USD", None, ctx.CancellationToken)
                    delegated <- delegated + 1
                    writeLine (sprintf "delegated sub-question %d with USD:%0.2f" i grant)
                else
                    skipped <- skipped + 1
                    writeLine (sprintf "skipped sub-question %d; remaining USD:%0.2f" i remaining)

            return jsonObj {| delegated = delegated; skipped = skipped |}
        }

let private workerAgent : ArcpAgentHandler =
    fun ctx ->
        task {
            for phase in [ "gather"; "analyze"; "summarize" ] do
                do! ctx.ValidateOpAsync(Capabilities.ToolCall, "llm.complete", ctx.CancellationToken)
                do! ctx.EmitMetricAsync(sprintf "cost.%s" phase, 0.04m, Some "USD", None, ctx.CancellationToken)
            return jsonString "worker complete"
        }

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let parentLease =
                Lease.empty
                |> Lease.withCapability Capabilities.CostBudget [ "USD:0.30" ]
                |> Lease.withCapability Capabilities.ToolCall [ "llm.complete" ]
                |> Lease.withCapability Capabilities.AgentDelegate [ "worker" ]

            let! pair =
                connect
                    (fun server ->
                        server.RegisterAgent("planner", plannerAgent)
                        server.RegisterAgent("worker", workerAgent))
                    (Set.ofList [ Features.CostBudget ])

            let! handle =
                pair.Client.SubmitAsync(
                    { Agent = "planner"
                      Input = jsonObj {| question = "What changed in ARCP v1.1?" |}
                      LeaseRequest = Some parentLease
                      LeaseConstraints = None
                      IdempotencyKey = None
                      MaxRuntimeSec = None },
                    CancellationToken.None)

            let! result = handle.Result
            match result with
            | Ok payload -> writeLine (sprintf "planner result: %s" (payload.Result |> Option.map _.GetRawText() |> Option.defaultValue "null"))
            | Error err -> writeErr (sprintf "planner failed: %s" (ARCPError.code err))

            do! teardown pair
            return 0
        })
