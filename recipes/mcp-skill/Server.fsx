#!/usr/bin/env dotnet-script
// mcp-skill — Server
//
// Exposes an ARCP agent as an MCP (Model Context Protocol) tool so any
// MCP host (Claude Desktop, Cursor, Continue, ...) can invoke it without
// knowing anything about ARCP.
//
// Architecture:
//
//   MCP host ──stdio──► this process ──WebSocket──► ARCP runtime
//
// The process maintains one long-lived ArcpClient.  Each MCP
// CallToolRequest becomes an ARCP job.submit; the result is forwarded
// back as an MCP CallToolResult.
//
// Run:
//   dotnet script Server.fsx
//
// Requires:
//   dotnet add package Arcp, 1.0.0
//   dotnet add package ModelContextProtocol, 0.1.0  (official C# MCP SDK)

#r "nuget: Arcp, 1.0.0"
#r "nuget: ModelContextProtocol, 0.1.0"

open System
open System.Net.WebSockets
open System.Text.Json
open System.Threading
open ARCP.Public
open ModelContextProtocol.Server
open ModelContextProtocol.Protocol.Types

// ---------------------------------------------------------------------------
// Shared ARCP client — one connection to the runtime for the process lifetime
// ---------------------------------------------------------------------------

let arcpUrl = Environment.GetEnvironmentVariable("ARCP_URL") |> Option.ofObj |> Option.defaultValue "ws://localhost:5001/arcp"
let arcpToken = Environment.GetEnvironmentVariable("ARCP_TOKEN") |> Option.ofObj

let connectClient () =
    task {
        let ws = new ClientWebSocket()
        do! ws.ConnectAsync(Uri(arcpUrl), CancellationToken.None)
        let transport = WebSocketTransport.fromClientSocket ws
        let opts =
            { ArcpClientOptions.defaults with
                Token = arcpToken }
        return new ArcpClient(opts), transport
    }

// ---------------------------------------------------------------------------
// MCP server definition
// ---------------------------------------------------------------------------

let mcp =
    McpServer(
        McpServerOptions(
            ServerInfo = { Name = "arcp-bridge"; Version = "1.0.0" },
            Capabilities = ServerCapabilities(Tools = ToolsCapability())))

// --- List tools ---

mcp.SetRequestHandler(
    McpMethods.Tools.List,
    fun _req _ct ->
        task {
            return
                { Tools =
                    [| { Name        = "research"
                         Description = "Run a multi-step research job and return a written summary."
                         InputSchema  =
                            {| ``type``     = "object"
                               properties   =
                                 {| question   = {| ``type`` = "string"; description = "The research question to answer." |}
                                    budget_usd = {| ``type`` = "number"; description = "Maximum USD to spend (default 0.10)." |} |}
                               required     = [| "question" |] |}
                            |> JsonSerializer.SerializeToElement } |] }
        })

// --- Call tool ---

mcp.SetRequestHandler(
    McpMethods.Tools.Call,
    fun (req: CallToolRequest) ct ->
        task {
            let name = req.Params.Name
            if name <> "research" then
                return
                    { Content  = [| { Type = "text"; Text = sprintf "Unknown tool: %s" name } |]
                      IsError  = true }
            else

            let args = req.Params.Arguments |> Option.defaultValue (JsonSerializer.SerializeToElement({| |}))
            let question   = if args.TryGetProperty("question") |> fst then args.GetProperty("question").GetString() else "General research"
            let budgetUsd  = if args.TryGetProperty("budget_usd") |> fst then args.GetProperty("budget_usd").GetDecimal() else 0.10m

            // Submit ARCP job
            let! (client, transport) = connectClient ()
            use _client = client

            let handle =
                client.Submit(
                    transport,
                    { JobSubmitRequest.defaults with
                        Agent = "planner"
                        Input = JsonSerializer.SerializeToElement({| question = question |})
                        Lease =
                            Some {
                                Capabilities =
                                    Map.ofList
                                        [ "cost.budget",    [ sprintf "USD:%.2f" budgetUsd ]
                                          "tool.call",      [ "llm.complete" ]
                                          "agent.delegate", [ "worker" ] ] } },
                    ct)

            let! result = handle.Result

            return
                match result with
                | Ok output ->
                    let text = output.GetRawText()
                    { Content = [| { Type = "text"; Text = text } |]; IsError = false }
                | Error err ->
                    { Content = [| { Type = "text"; Text = sprintf "ARCP error: %s" (err.ToString()) } |]; IsError = true }
        })

// ---------------------------------------------------------------------------
// Run over stdio (MCP standard transport)
// ---------------------------------------------------------------------------

printfn "ARCP MCP bridge starting (stdio transport)" |> ignore

let stdioTransport = McpStdioServerTransport()
mcp.ConnectAsync(stdioTransport, CancellationToken.None) |> Async.AwaitTask |> Async.RunSynchronously
