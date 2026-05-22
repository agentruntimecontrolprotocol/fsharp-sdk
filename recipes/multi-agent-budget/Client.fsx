#!/usr/bin/env dotnet-script
// multi-agent-budget — Client
//
// Submits a research question to the planner agent with a $0.50 USD
// budget cap.  Prints a live cost tally as metric events arrive.
//
// Run:
//   dotnet script Client.fsx

#r "nuget: Arcp, 1.0.0"

open System
open System.Net.WebSockets
open System.Text.Json
open System.Threading
open ARCP.Public

let run () =
    task {
        let ct = CancellationToken.None
        let ws = new ClientWebSocket()
        do! ws.ConnectAsync(Uri("ws://localhost:5001/arcp"), ct)
        let transport = WebSocketTransport.fromClientSocket ws

        use client = new ArcpClient(ArcpClientOptions.defaults)

        let handle =
            client.Submit(
                transport,
                { JobSubmitRequest.defaults with
                    Agent = "planner"
                    Input = JsonSerializer.SerializeToElement({| question = "What are the latest advances in quantum error correction?" |})
                    Lease =
                        Some {
                            Capabilities =
                                Map.ofList
                                    [ "cost.budget",    [ "USD:0.50" ]
                                      "tool.call",      [ "llm.complete" ]
                                      "agent.delegate", [ "worker" ] ] } },
                ct)

        printfn "Submitted job %s" (handle.JobId.ToString())
        printfn ""

        let mutable totalCost = 0.0
        let mutable delegations = 0

        for event in handle.Events do
            match event.Body with
            | JobEventBody.Status(state, msg) ->
                printfn "[status] %s  %s" state (defaultArg msg "")
            | JobEventBody.Metric(name, value, unit, dims) ->
                totalCost <- totalCost + value
                let dimStr = dims |> Map.toSeq |> Seq.map (fun (k,v) -> sprintf "%s=%s" k v) |> String.concat " "
                printfn "[metric] %s  %.4f %s  %s" name value unit dimStr
                printfn "         running total: $%.4f USD" totalCost
            | JobEventBody.Delegate(agent, _input, _lease, callId) ->
                delegations <- delegations + 1
                printfn "[delegate] → %s  call_id=%s" agent callId
            | _ -> ()

        let! result = handle.Result
        match result with
        | Ok output ->
            printfn ""
            printfn "=== Result ==="
            printfn "%s" (output.GetProperty("answer").GetString())
            printfn ""
            printfn "Total delegations: %d" delegations
            printfn "Total cost:        $%.4f USD" totalCost
        | Error err ->
            printfn "Job failed: %s" (err.ToString())
    }

run () |> Async.AwaitTask |> Async.RunSynchronously
