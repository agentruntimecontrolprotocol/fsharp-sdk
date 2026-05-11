/// ARCP runtime fronting an MCP server (RFC §20).
///
/// MCP describes capabilities; ARCP operationalizes them. This bridge
/// translates inbound ARCP `tool.invoke` envelopes into MCP `call_tool`
/// calls against an upstream MCP server, and emits the ARCP job lifecycle
/// back to the calling client.
///
///   ARCP client ──tool.invoke──> bridge ──call_tool──> MCP server
///   ARCP client <─job.{accepted,started,completed,failed}─ bridge
module ARCP.Samples.Mcp.Program

open System.Text.Json
open System.Threading.Tasks
open FSharp.Control
open ARCP.Envelope
open ARCP.Errors
open ARCP.Ids
open ARCP.Samples.Mcp.Upstream

// TODO: replace with vendored bridge — official C# MCP SDK pending.
// Stub the surface we'd use so the bridge logic stays the focus.
type McpToolDescriptor = { Name: string }

type McpCallResult = { IsError: bool; Content: JsonElement }

type ClientSession() =
    member _.InitializeAsync() : Task<unit> =
        task { return failwith "elided: MCP initialize" }

    member _.ListToolsAsync() : Task<McpToolDescriptor list> =
        task { return failwith "elided: MCP tools/list" }

    member _.CallToolAsync(name: string, arguments: JsonElement) : Task<McpCallResult> =
        task { return failwith "elided: MCP call_tool" }

let stdioClient (p: StdioServerParameters) : Task<ClientSession> =
    task { return failwith "elided: launch stdio MCP server" }

// Per RFC §20:
//   MCP tool schema -> ARCP capability  (advertised at session.accepted)
//   MCP tool call   -> ARCP job
//   MCP resource    -> ARCP stream of kind: event  (delegated to MCP)

/// MCP `tools/list` → namespaced ARCP capability extensions.
let advertiseFromMcp (mcp: ClientSession) : Task<string list> =
    task {
        let! listed = mcp.ListToolsAsync()
        return listed |> List.map (fun t -> sprintf "arcpx.mcp.tool.%s.v1" t.Name)
    }

/// Translate ARCP `tool.invoke.payload` into MCP `call_tool`.
/// MCP errors become canonical ARCP error codes.
let callViaMcp (mcp: ClientSession) (tool: string) (arguments: JsonElement) : Task<JsonElement> =
    task {
        try
            let! result = mcp.CallToolAsync(tool, arguments)

            if result.IsError then
                // MCP doesn't carry a typed error code; FAILED_PRECONDITION is the right
                // canonical mapping for "tool ran, said no".
                return raise (exn (ARCPError.message (FailedPrecondition(string result.Content))))
            else
                return result.Content
        with ex ->
            return raise (exn (ARCPError.message (Internal(ex.Message, Some ex))))
    }

type SendEnvelope = Envelope<JsonElement> -> Task

/// One inbound ARCP `tool.invoke` → MCP call → ARCP job lifecycle.
let handleInvoke (send: SendEnvelope) (mcp: ClientSession) (request: Envelope<JsonElement>) : Task =
    task {
        let jobId = JobId.create ()

        // send job.accepted (correlated to request.Id)
        // send job.started

        try
            let tool = request.Payload.GetProperty("tool").GetString()

            let arguments =
                match request.Payload.TryGetProperty "arguments" with
                | true, v -> v
                | _ -> JsonDocument.Parse("{}").RootElement

            let! _result = callViaMcp mcp tool arguments
            // send job.completed { result = _result }
            return ()
        with _ex ->
            // send job.failed { code; message; ... }
            return ()
    }
    :> Task

/// Wire one MCP session as the upstream for one ARCP runtime.
let runBridge (send: SendEnvelope) (inbound: IAsyncEnumerable<Envelope<JsonElement>>) : Task =
    task {
        let! mcp = stdioClient (upstreamParams ())
        do! mcp.InitializeAsync()
        let! extensions = advertiseFromMcp mcp
        // In production this list would feed `Capabilities.Extensions` at the
        // runtime's `session.accepted` so clients negotiate exactly the MCP
        // tools they expect to use.
        printfn "bridged: %A" extensions

        do!
            inbound
            |> TaskSeq.iterAsync (fun env ->
                task {
                    if env.Type = "tool.invoke" then
                        do! handleInvoke send mcp env
                })
    }
    :> Task

[<EntryPoint>]
let main _ =
    // Production version: instantiate an `ARCP.Runtime.Runtime`, point its
    // tool-invoke handler at `handleInvoke`, and let the WebSocket transport
    // carry inbound envelopes from real ARCP clients. Wiring is elided so this
    // file stays focused on the §20 translation between protocols.
    let send: SendEnvelope = fun _ -> failwith "elided: bound to runtime outbound"

    let inbound: IAsyncEnumerable<Envelope<JsonElement>> =
        failwith "elided: runtime inbound"

    (runBridge send inbound).GetAwaiter().GetResult()
    0
