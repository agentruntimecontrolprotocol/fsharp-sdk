module ArcpRecipes.McpSkill

open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime
open ArcpRecipes.RecipeHarness

let private plannerAgent : ArcpAgentHandler =
    fun ctx ->
        task {
            do! ctx.EmitStatusAsync("planning", Some "decomposing research request", ctx.CancellationToken)
            let body: DelegateBody = {
                ChildJobId = (JobId.newId()).Value
                Agent = "worker"
                Lease =
                    Lease.empty
                    |> Lease.withCapability Capabilities.ToolCall [ "llm.complete" ]
                    |> Lease.withCapability Capabilities.CostBudget [ "USD:0.10" ]
                LeaseConstraints = None
            }
            do! ctx.EmitDelegateAsync(body, ctx.CancellationToken)
            do! ctx.EmitMetricAsync("cost.plan", 0.03m, Some "USD", None, ctx.CancellationToken)
            return jsonObj {| answer = "Bridge submitted research job"; delegated = 1 |}
        }

let private callResearchTool (client: ArcpClient) question budgetUsd =
    task {
        let lease =
            Lease.empty
            |> Lease.withCapability Capabilities.CostBudget [ sprintf "USD:%0.2f" budgetUsd ]
            |> Lease.withCapability Capabilities.ToolCall [ "llm.complete" ]
            |> Lease.withCapability Capabilities.AgentDelegate [ "worker" ]

        let! handle =
            client.SubmitAsync(
                { Agent = "planner"
                  Input = jsonObj {| question = question |}
                  LeaseRequest = Some lease
                  LeaseConstraints = None
                  IdempotencyKey = None
                  MaxRuntimeSec = None },
                CancellationToken.None)

        let! result = handle.Result
        return
            match result with
            | Ok payload -> payload.Result |> Option.map _.GetRawText() |> Option.defaultValue "null"
            | Error err -> sprintf """{"error":"%s"}""" (ARCPError.code err)
    }

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let! pair =
                connect
                    (fun server -> server.RegisterAgent("planner", plannerAgent))
                    (Set.ofList [ Features.CostBudget ])

            let! response = callResearchTool pair.Client "Summarize ARCP resumability" 0.50m
            writeLine (sprintf "MCP tool response: %s" response)

            do! teardown pair
            return 0
        })
