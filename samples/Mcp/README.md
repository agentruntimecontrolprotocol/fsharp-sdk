# Mcp

ARCP runtime that fronts an MCP server. Inbound `tool.invoke`
envelopes translate to MCP `call_tool`; the bridge emits the ARCP
job lifecycle back to the calling client.

## Before ARCP

You either (a) ditch your ARCP-native session/lease/observability
story and run MCP straight, losing the runtime layer; or (b) embed
MCP into one specific agent that knows how to call it directly,
which doesn't compose with the rest of your stack. Wrap one,
re-wrap the other.

## With ARCP

Per RFC §20:

| MCP         | ARCP                                          |
|-------------|-----------------------------------------------|
| tool schema | capability (`arcpx.mcp.tool.<name>.v1`)       |
| tool call   | job (`tool.invoke` → `job.completed`)         |
| resource    | stream of `kind: event` (delegated)           |

The bridge advertises the upstream server's tools as namespaced
capability extensions at session open. Clients that need a specific
MCP tool refuse the session if it's not advertised — same shape as
any other ARCP capability negotiation.

```fsharp
let! mcp = stdioClient (upstreamParams ())
do! mcp.InitializeAsync ()
let! extensions = advertiseFromMcp mcp
do!
    inbound
    |> TaskSeq.iterAsync (fun env ->
        task {
            if env.Type = "tool.invoke" then
                do! handleInvoke send mcp env
        })
```

`callViaMcp` translates MCP errors into canonical ARCP error codes
(`FAILED_PRECONDITION` for `result.IsError`, `INTERNAL` for
unexpected exceptions at the boundary).

## ARCP primitives

- MCP compatibility — RFC §20 (the whole point).
- `tool.invoke` / `job.accepted` / `job.started` /
  `job.completed` / `job.failed` lifecycle — §6.3, §10.
- Capability extensions for advertised tools — §7, §21.
- Canonical error mapping — §18.2.

## File tour

- `Program.fs` — the bridge. `handleInvoke` is the file. Runtime
  wiring (transport, session manager) is symmetric with
  `ARCP.Runtime.Runtime` and elided.
- `Upstream.fs` — MCP server invocation params.

## Notes

There is no stable .NET MCP SDK at the time of writing; the
`ClientSession` / `stdioClient` types in `Program.fs` are stubs.
Replace them with the vendored bridge once an official C# / F# MCP
client lands.

## Variations

- Front multiple MCP servers from one ARCP runtime; namespace each
  set of tools under `arcpx.mcp.<server>.tool.<name>.v1`.
- Bridge MCP resources to ARCP streams of `kind: event` so ARCP
  observers can subscribe to MCP resource changes.
- Layer ARCP leases on top: gate `tool.invoke` for any
  side-effecting MCP tool through `permission.request` before
  forwarding to MCP.
