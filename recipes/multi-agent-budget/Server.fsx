#!/usr/bin/env dotnet-script
// multi-agent-budget — Server
//
// A two-agent pipeline that demonstrates ARCP cost budgets.
//
//   planner  — decomposes a research question into sub-questions,
//              delegates each to a worker, and merges the answers.
//   worker   — answers a single question in three LLM passes
//              (gather → analyse → summarise) with per-step cost tracking.
//
// Cost model (illustrative):
//   Each LLM completion costs $0.005.
//   The planner budgets each worker from a table keyed on delegation depth.
//
// Run:
//   dotnet script Server.fsx

#r "nuget: Arcp, 1.0.0"
#r "nuget: Microsoft.AspNetCore.App.Ref, 10.0.0"

open System
open System.Text.Json
open System.Threading
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open ARCP.Public

// ---------------------------------------------------------------------------
// Budget constants
// ---------------------------------------------------------------------------

/// Per-depth USD grant for sub-agents.
let GRANT_BY_DEPTH =
    Map.ofList [ 1, 0.05m; 2, 0.10m; 3, 0.15m ]

/// Cost per simulated LLM completion call.
let COST_PER_COMPLETION = 0.005m

// ---------------------------------------------------------------------------
// Simulated LLM call
// ---------------------------------------------------------------------------

let simulateLlm (prompt: string) (ct: CancellationToken) =
    task {
        do! System.Threading.Tasks.Task.Delay(50, ct)
        return sprintf "Simulated answer to: %s" prompt
    }

// ---------------------------------------------------------------------------
// Worker agent
// ---------------------------------------------------------------------------

let workerHandler (ctx: JobContext) =
    task {
        let ct = ctx.CancellationToken
        let question = ctx.Input.GetProperty("question").GetString()

        // Phase 1 — gather
        let! permitted1 = ctx.ValidateOpAsync("tool.call", "llm.complete", ct)
        if not permitted1 then
            return JsonSerializer.SerializeToElement({| error = "llm.complete not in lease" |})
        else

        do! ctx.EmitStatusAsync("gathering", Some "Phase 1: gathering sources", ct)
        let! raw = simulateLlm ("Gather sources for: " + question) ct
        do! ctx.EmitMetricAsync("cost.completion", float COST_PER_COMPLETION, "USD", Map.empty, ct)

        // Phase 2 — analyse
        let! permitted2 = ctx.ValidateOpAsync("tool.call", "llm.complete", ct)
        if not permitted2 then
            return JsonSerializer.SerializeToElement({| error = "llm.complete revoked mid-job" |})
        else

        do! ctx.EmitStatusAsync("analysing", Some "Phase 2: analysing data", ct)
        let! analysis = simulateLlm ("Analyse: " + raw) ct
        do! ctx.EmitMetricAsync("cost.completion", float COST_PER_COMPLETION, "USD", Map.empty, ct)

        // Phase 3 — summarise
        let! permitted3 = ctx.ValidateOpAsync("tool.call", "llm.complete", ct)
        if not permitted3 then
            return JsonSerializer.SerializeToElement({| error = "llm.complete revoked mid-job" |})
        else

        do! ctx.EmitStatusAsync("summarising", Some "Phase 3: writing summary", ct)
        let! summary = simulateLlm ("Summarise: " + analysis) ct
        do! ctx.EmitMetricAsync("cost.completion", float COST_PER_COMPLETION, "USD", Map.empty, ct)

        return JsonSerializer.SerializeToElement({| question = question; answer = summary |})
    }

// ---------------------------------------------------------------------------
// Planner agent
// ---------------------------------------------------------------------------

let plannerHandler (ctx: JobContext) =
    task {
        let ct = ctx.CancellationToken
        let question = ctx.Input.GetProperty("question").GetString()
        let depth = if ctx.Input.TryGetProperty("depth") |> fst then ctx.Input.GetProperty("depth").GetInt32() else 1

        do! ctx.EmitStatusAsync("planning", Some "Decomposing question into sub-questions", ct)

        // Decompose via LLM
        let! decomposed = simulateLlm ("Split into 2 sub-questions: " + question) ct
        do! ctx.EmitMetricAsync("cost.completion", float COST_PER_COMPLETION, "USD", Map.empty, ct)

        let subQuestions =
            [| sprintf "Sub-question A of: %s" question
               sprintf "Sub-question B of: %s" question |]

        let grant =
            Map.tryFind depth GRANT_BY_DEPTH
            |> Option.defaultValue 0.05m

        let remainingBudget = ctx.Budget |> Map.tryFind "USD" |> Option.defaultValue 0m
        let answers = ResizeArray<string>()

        for subQ in subQuestions do
            if remainingBudget < grant then
                printfn "Planner: skipping '%s' — insufficient budget (%.3f < %.3f)" subQ remainingBudget grant
            else
                // Validate delegation permission
                let! permitted = ctx.ValidateOpAsync("agent.delegate", "worker", ct)
                if not permitted then
                    do! ctx.EmitStatusAsync("warning", Some (sprintf "agent.delegate/worker denied for sub-question: %s" subQ), ct)
                else
                    // Delegate to worker
                    let delegateInput = JsonSerializer.SerializeToElement({| question = subQ; depth = depth + 1 |})
                    let delegateLease = { Capabilities = Map.ofList [ "tool.call", [ "llm.complete" ]; "cost.budget", [ sprintf "USD:%.3f" grant ] ] }

                    do! ctx.EmitRawBodyAsync(
                            JobEventBody.Delegate(
                                agent = "worker",
                                input = delegateInput,
                                lease = Some delegateLease,
                                callId = Guid.NewGuid().ToString("N")),
                            ct)

                    // Debit cost from our own budget
                    do! ctx.EmitMetricAsync("cost.delegate", float grant, "USD", Map.ofList [ "agent", "worker" ], ct)

                    answers.Add(sprintf "Worker answer for '%s': %s" subQ "(simulated)")

        let! merged = simulateLlm (sprintf "Merge answers: %s" (String.concat " | " answers)) ct
        do! ctx.EmitMetricAsync("cost.completion", float COST_PER_COMPLETION, "USD", Map.empty, ct)

        ignore decomposed
        return JsonSerializer.SerializeToElement({| question = question; answer = merged; sub_answers = answers.ToArray() |})
    }

// ---------------------------------------------------------------------------
// Wire up and serve
// ---------------------------------------------------------------------------

let server = new ArcpServer(ArcpServerOptions.defaults)
server.RegisterAgent("planner", plannerHandler)
server.RegisterAgent("worker",  workerHandler)

let builder = WebApplication.CreateBuilder()
builder.Services.AddArcp() |> ignore

let app = builder.Build()
app.UseWebSockets()
app.MapArcp("/arcp")

printfn "Multi-agent budget server listening on ws://localhost:5001/arcp"
app.Run("http://localhost:5001")
