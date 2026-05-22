#!/usr/bin/env dotnet-script
// email-vendor-leases — Server
//
// An ARCP agent that triages an email inbox using Claude.  The agent can
// read emails (granted in its lease) but cannot send them (not granted),
// so it returns a drafted reply instead.  For every message it reads it
// emits an x-vendor.acme.email.parsed event so the client can observe
// structured metadata without parsing raw text.
//
// Run:
//   dotnet script Server.fsx
//
// Requires:
//   dotnet add package Arcp
//   dotnet add package Anthropic.SDK   (or your preferred Claude client)

#r "nuget: Arcp, 1.0.0"
#r "nuget: Anthropic.SDK, 4.4.0"
#r "nuget: Microsoft.AspNetCore.App.Ref, 10.0.0"

open System
open System.Net.WebSockets
open System.Text.Json
open System.Threading
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open ARCP.Public
open Anthropic.SDK
open Anthropic.SDK.Messaging

// ---------------------------------------------------------------------------
// Stub inbox — replace with your real email client
// ---------------------------------------------------------------------------

type Email = { Id: string; From: string; Subject: string; Body: string; Date: DateTimeOffset }

let inbox =
    [| { Id = "msg-001"; From = "alice@example.com"; Subject = "Q3 invoice"; Body = "Hi, attached is the Q3 invoice. Please approve by Friday."; Date = DateTimeOffset.UtcNow.AddHours(-2.0) }
       { Id = "msg-002"; From = "bob@example.com"; Subject = "Meeting reschedule"; Body = "Could we move tomorrow's 2pm to 4pm?"; Date = DateTimeOffset.UtcNow.AddHours(-1.0) } |]

let inboxList () =
    inbox |> Array.map (fun e -> {| id = e.Id; from = e.From; subject = e.Subject; date = e.Date |})

let inboxRead (id: string) =
    inbox |> Array.tryFind (fun e -> e.Id = id)

// ---------------------------------------------------------------------------
// Claude tool definitions
// ---------------------------------------------------------------------------

let tools =
    [| Tool(
           Name = "inbox_list",
           Description = "Return a summary list of all emails in the inbox.",
           InputSchema = {| ``type`` = "object"; properties = {||}; required = [||] |} |> JsonSerializer.SerializeToElement)
       Tool(
           Name = "inbox_read",
           Description = "Read the full body of a single email.",
           InputSchema = {| ``type`` = "object"; properties = {| id = {| ``type`` = "string"; description = "Email id from inbox_list" |} |}; required = [| "id" |] |} |> JsonSerializer.SerializeToElement)
       Tool(
           Name = "send_reply",
           Description = "Send a reply to an email.",
           InputSchema = {| ``type`` = "object"; properties = {| id = {| ``type`` = "string" |}; body = {| ``type`` = "string" |} |}; required = [| "id"; "body" |] |} |> JsonSerializer.SerializeToElement) |]

// ---------------------------------------------------------------------------
// Agent handler
// ---------------------------------------------------------------------------

let anthropic = AnthropicClient(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))

let triageHandler (ctx: JobContext) =
    task {
        let ct = ctx.CancellationToken
        let messages = ResizeArray<Message>()
        messages.Add(Message(Role = "user", Content = [| TextContent("Triage the inbox. Read each email and draft a polite reply. Do not send anything.") |]))

        let mutable finalDraft = ""
        let mutable loop = true

        while loop do
            let req =
                MessageParameters(
                    Model = "claude-opus-4-7",
                    MaxTokens = 1024,
                    Messages = messages.ToArray(),
                    Tools = tools)

            let! resp = anthropic.Messages.GetClaudeMessageAsync(req, ct)

            messages.Add(Message(Role = "assistant", Content = resp.Content))

            if resp.StopReason = "end_turn" then
                // Extract final text
                for block in resp.Content do
                    match block with
                    | :? TextContent as t -> finalDraft <- t.Text
                    | _ -> ()
                loop <- false
            else
                // Process tool_use blocks
                let toolResults = ResizeArray<ContentBase>()

                for block in resp.Content do
                    match block with
                    | :? ToolUseContent as tu ->
                        let callId = tu.Id
                        let toolName = tu.Name
                        let args = tu.Input

                        // Validate against the lease before running
                        let! permitted = ctx.ValidateOpAsync("tool.call", toolName, ct)

                        if not permitted then
                            // Emit tool result error so Claude can continue gracefully
                            do! ctx.EmitToolCallAsync(toolName, args, callId, ct)
                            do! ctx.EmitToolResultAsync(callId, ToolOutcome.Error, JsonSerializer.SerializeToElement("Permission denied: tool.call not granted for " + toolName), ct)
                            toolResults.Add(ToolResultContent(ToolUseId = callId, Content = [| TextContent("denied: tool.call/" + toolName + " is not in your lease") |]))
                        else
                            do! ctx.EmitToolCallAsync(toolName, args, callId, ct)

                            let resultElement =
                                match toolName with
                                | "inbox_list" ->
                                    inboxList () |> JsonSerializer.SerializeToElement

                                | "inbox_read" ->
                                    let id = args.GetProperty("id").GetString()
                                    match inboxRead id with
                                    | None ->
                                        JsonSerializer.SerializeToElement("Email not found")
                                    | Some email ->
                                        // Emit vendor event with parsed metadata
                                        let parsed =
                                            {| message_id = email.Id
                                               from = email.From
                                               subject = email.Subject
                                               date = email.Date
                                               word_count = email.Body.Split(' ').Length |}
                                            |> JsonSerializer.SerializeToElement
                                        do! ctx.EmitRawBodyAsync(JobEventBody.Vendor("x-vendor.acme.email.parsed", parsed), ct)
                                        JsonSerializer.SerializeToElement(email)

                                | _ ->
                                    JsonSerializer.SerializeToElement("Unknown tool")

                            do! ctx.EmitToolResultAsync(callId, ToolOutcome.Ok, resultElement, ct)
                            toolResults.Add(ToolResultContent(ToolUseId = callId, Content = [| TextContent(resultElement.GetRawText()) |]))

                    | _ -> ()

                messages.Add(Message(Role = "user", Content = toolResults.ToArray()))

        return JsonSerializer.SerializeToElement({| drafted_reply = finalDraft; sent = false |})
    }

// ---------------------------------------------------------------------------
// Wire up and serve
// ---------------------------------------------------------------------------

let server = new ArcpServer(ArcpServerOptions.defaults)
server.RegisterAgent("triage", triageHandler)

let builder = WebApplication.CreateBuilder()
builder.Services.AddArcp() |> ignore

let app = builder.Build()
app.UseWebSockets()
app.MapArcp("/arcp")

printfn "Email triage server listening on ws://localhost:5000/arcp"
app.Run("http://localhost:5000")
