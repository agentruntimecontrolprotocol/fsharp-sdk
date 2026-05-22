# Recipes

Self-contained F# script examples that demonstrate common ARCP patterns.
Each recipe is a pair (or trio) of `.fsx` files you can run directly with
`dotnet script`.

## Prerequisites

```sh
dotnet tool install -g dotnet-script
```

Then restore packages for a script:

```sh
cd recipes/<recipe>
dotnet script restore Server.fsx
```

---

## email-vendor-leases

**Constrained tool access + vendor events**

A Claude-powered email triage agent that can read emails but cannot send
them.  The client grants a lease for `tool.call: [inbox_list, inbox_read]`
and deliberately omits `send_reply`.  When Claude tries to call `send_reply`
the runtime returns `PERMISSION_DENIED` and the agent gracefully falls back
to drafting a reply instead.

For every email it reads the agent emits an `x-vendor.acme.email.parsed`
event so the client receives structured metadata (word count, date, sender)
without parsing raw text.

| File | Description |
| ---- | ----------- |
| [Server.fsx](email-vendor-leases/Server.fsx) | Triage agent — Claude tool-use loop with lease validation |
| [Client.fsx](email-vendor-leases/Client.fsx) | Submit with read-only lease; print vendor events |

```sh
cd email-vendor-leases
dotnet script Server.fsx &
dotnet script Client.fsx
```

---

## multi-agent-budget

**Cascaded cost budgets across a planner/worker hierarchy**

A planner agent decomposes a research question into sub-questions and
delegates each to a worker.  The worker performs three LLM passes (gather →
analyse → summarise) and emits a `cost.completion` metric after each call.
The planner emits `cost.delegate` when it budgets a sub-agent, ensuring the
total spend never exceeds the client's USD cap.

| File | Description |
| ---- | ----------- |
| [Server.fsx](multi-agent-budget/Server.fsx) | Planner + worker agents with depth-keyed grant table |
| [Client.fsx](multi-agent-budget/Client.fsx) | Submit with `cost.budget: [USD:0.50]`; live cost tally |

```sh
cd multi-agent-budget
dotnet script Server.fsx &
dotnet script Client.fsx
```

---

## stream-resume

**Streaming output with mid-stream reconnect**

A long-form writer agent streams its article as `job.stream_chunk` events
(~200 chars per chunk).  The client intentionally drops the transport after
800 ms to simulate a network interruption, then reconnects in a second
session and calls `client.ResumeAsync` with the `last_event_seq` it
received.  The server replays any missed chunks from its 60-second event log
before continuing the live stream.

The client reassembles all chunks in `chunk_seq` order and prints the
complete article.

| File | Description |
| ---- | ----------- |
| [Server.fsx](stream-resume/Server.fsx) | Writer with `BeginStreamingResult` + `EmitResultChunkAsync` |
| [Client.fsx](stream-resume/Client.fsx) | Disconnect after 800 ms, resume, reassemble |

```sh
cd stream-resume
dotnet script Server.fsx &
dotnet script Client.fsx
```

---

## mcp-skill

**Expose an ARCP agent as an MCP tool**

A bridge process that speaks [MCP](https://modelcontextprotocol.io) over
`stdio` and forwards every `CallToolRequest` to an ARCP runtime over
WebSocket.  Once registered with an MCP host (Claude Desktop, Cursor, …)
the host can invoke the `research` tool without knowing anything about ARCP.

One long-lived `ArcpClient` is shared across all MCP requests in the
process lifetime.

| File | Description |
| ---- | ----------- |
| [Server.fsx](mcp-skill/Server.fsx) | MCP stdio bridge → ARCP job.submit |

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "arcp-research": {
      "command": "dotnet",
      "args": ["script", "/path/to/recipes/mcp-skill/Server.fsx"],
      "env": {
        "ARCP_URL": "ws://localhost:5001/arcp",
        "ARCP_TOKEN": "your-token"
      }
    }
  }
}
```

---

## Related

- [Jobs guide](../docs/guides/jobs.md) — submit, cancel, idempotency keys
- [Leases guide](../docs/guides/leases.md) — capabilities, globs, validation
- [Job events guide](../docs/guides/job-events.md) — all event types
- [Vendor extensions guide](../docs/guides/vendor-extensions.md) — `x-vendor.*` events
- [Resume guide](../docs/guides/resume.md) — event log, `ResumeAsync`
- [Delegation guide](../docs/guides/delegation.md) — `agent.delegate`, sub-agents
- [Observability guide](../docs/guides/observability.md) — metrics, OTel
