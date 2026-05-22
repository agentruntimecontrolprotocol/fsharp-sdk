#!/usr/bin/env dotnet-script
// email-vendor-leases — Client
//
// Submits a job to the triage agent with a read-only lease.
// The lease grants tool.call for inbox_list and inbox_read but NOT
// send_reply, so the agent can only draft a reply, not send it.
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
        do! ws.ConnectAsync(Uri("ws://localhost:5000/arcp"), ct)
        let transport = WebSocketTransport.fromClientSocket ws

        use client = new ArcpClient(ArcpClientOptions.defaults)

        let handle =
            client.Submit(
                transport,
                { JobSubmitRequest.defaults with
                    Agent = "triage"
                    Input = JsonSerializer.SerializeToElement({| |})
                    // Grant only read tools — send_reply is intentionally absent
                    Lease = Some { Capabilities = Map.ofList [ "tool.call", [ "inbox_list"; "inbox_read" ] ] } },
                ct)

        printfn "Submitted job %s" (handle.JobId.ToString())
        printfn ""

        // Stream events to stdout
        let mutable vendorEvents = 0
        for event in handle.Events do
            match event.Body with
            | JobEventBody.Status(state, msg) ->
                printfn "[status] %s  %s" state (defaultArg msg "")
            | JobEventBody.ToolCall(name, args, callId) ->
                printfn "[tool.call] %s (call_id=%s)" name callId
            | JobEventBody.ToolResult(callId, outcome, _result) ->
                printfn "[tool.result] call_id=%s outcome=%A" callId outcome
            | JobEventBody.Vendor(kind, body) ->
                vendorEvents <- vendorEvents + 1
                printfn "[vendor] %s  %s" kind (body.GetRawText())
            | _ -> ()

        let! result = handle.Result
        match result with
        | Ok output ->
            let draft = output.GetProperty("drafted_reply").GetString()
            let sent  = output.GetProperty("sent").GetBoolean()
            printfn ""
            printfn "=== Result ==="
            printfn "Sent: %b" sent
            printfn "Drafted reply:"
            printfn "%s" draft
            printfn ""
            printfn "(%d x-vendor.acme.email.parsed events received)" vendorEvents
        | Error err ->
            printfn "Job failed: %s" (err.ToString())
    }

run () |> Async.AwaitTask |> Async.RunSynchronously
