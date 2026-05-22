# Observability (§11)

ARCP carries W3C trace context end to end. With `Arcp.Otel` wired on
both client and runtime, every envelope generates a span and every job
becomes a unit of work in your tracing backend.

## Trace propagation

Every envelope can carry a `trace_id` at the top level and a
`traceparent`/`tracestate` pair inside
`extensions["x-vendor.opentelemetry.tracecontext"]`. The OTel
middleware injects these on send and extracts them on receive — so the
runtime side starts a child span linked to the client's span.

```json
// envelope on the wire:
{
  "arcp": "1.1",
  "id": "01J…",
  "type": "job.submit",
  "trace_id": "0123456789abcdef0123456789abcdef",
  "payload": { "agent": "echo", "input": {} },
  "extensions": {
    "x-vendor.opentelemetry.tracecontext": {
      "traceparent": "00-0123…-…",
      "tracestate": "vendor=value"
    }
  }
}
```

## Setup

Add `Arcp.Otel` and call `UseArcpTracing` on both sides. The package
uses `ArcpActivitySource.Instance` — a shared `ActivitySource` named
`"ARCP"` at version `"1.0.0"`.

```fsharp
open ARCP.Otel
open OpenTelemetry

// Client side — wire tracing into the transport pipeline
let tracedTransport =
    transport |> ArcpOtel.withClientTracing tracerProvider

// Server side — wire tracing on every accepted session
let server =
    new ArcpServer(
        serverOptions,
        fun rawTransport ->
            let tracedTransport = ArcpOtel.withServerTracing rawTransport tracerProvider
            sessionHandler tracedTransport)
```

Or via the ASP.NET Core extension:

```fsharp
// Program.fs
builder.Services.AddArcp()
       .AddArcpTracing()  // adds Arcp.Otel to the pipeline

// OpenTelemetry SDK setup
builder.Services
    .AddOpenTelemetry()
    .WithTracing(fun builder ->
        builder
            .AddArcpInstrumentation()  // registers ArcpActivitySource.Instance
            .AddOtlpExporter() |> ignore)
```

## Span shape

`Arcp.Otel` emits two span types per envelope:

| Span        | Attributes                                                                  |
| ----------- | --------------------------------------------------------------------------- |
| `arcp.send` | `arcp.type`, `arcp.id`, `arcp.session_id`, `arcp.job_id?`, `arcp.event_seq?` |
| `arcp.recv` | same                                                                        |

For `job.submit` / `job.accepted` / `job.result` / `job.error`, the
middleware also attaches the `ArcpSpanAttributes` constants:

```fsharp
open ARCP.Otel

ArcpSpanAttributes.SessionId         // "arcp.session_id"
ArcpSpanAttributes.JobId             // "arcp.job_id"
ArcpSpanAttributes.Agent             // "arcp.agent"
ArcpSpanAttributes.LeaseCapabilities // "arcp.lease.capabilities"
ArcpSpanAttributes.LeaseExpiresAt    // "arcp.lease.expires_at"
ArcpSpanAttributes.BudgetRemaining   // "arcp.budget.remaining"
```

## Per-job spans

A job is a natural span boundary. Inside an agent handler, the active
`Activity` context is set by the OTel middleware from the incoming
`traceparent`; child activities nest automatically:

```fsharp
open System.Diagnostics
open ARCP.Otel

let source = ArcpActivitySource.Instance

server.RegisterAgent("report", fun ctx ->
    task {
        use activity = source.StartActivity("collect-sources")
        activity |> Option.iter (fun a -> a.SetTag("source.count", 5) |> ignore)

        do! ctx.EmitStatusAsync("collecting", None, ctx.CancellationToken)
        // … do work …

        return Json.serializeToElement<bool> true
    })
```

You don't need to thread the trace context manually — .NET's
`Activity` and `AsyncLocal` propagate it across `Task` hops, and the
middleware sets the context before invoking the agent handler.

## Delegation cascades

Children inherit the parent's `trace_id`. With `Arcp.Otel` wired on
both sides, every child job becomes a child span of the parent — your
observability backend reconstructs the orchestration tree
automatically.

```
client submit
  └─ arcp.send job.submit                 (trace_id = 0123…)
       └─ arcp.recv job.submit
            └─ job.run orchestrator        (span, same trace)
                 ├─ arcp.send job.accepted (child job)
                 │    └─ job.run pdf-renderer
                 └─ arcp.send job.accepted (second child)
                      └─ job.run summarizer
```

See [delegation guide](delegation.md#trace-propagation).

## Manual trace_id (without Arcp.Otel)

If you don't want the full OTel package, set `trace_id` manually on
every submit. It is just a 32-hex string; the runtime propagates it to
all events and to any children spawned via delegate:

```fsharp
open ARCP.Core

let traceId = Guid.NewGuid().ToString("N")  // 32-hex

let request = {
    Agent = "research"
    Input = Json.serializeToElement<{| topic: string |}> {| topic = "F# 9" |}
    TraceId = Some traceId
    LeaseRequest = None
    LeaseConstraints = None
    IdempotencyKey = None
    MaxRuntimeSec = None
}
```

Use `trace_id` for log correlation even without distributed tracing.

## Heartbeats vs spans

The v1.1 heartbeat (§6.4) is for keep-alive, not observability. Don't
emit a span per heartbeat — it's high-frequency, low-value noise. The
OTel middleware filters out `session.heartbeat` / `session.pong` by
default.

## Sampling

OTel sampling is your call — the middleware emits activities into
whatever `ActivitySource` your application configures. For
high-throughput runtimes, sample at the collector rather than at the
SDK to keep parent/child relationships intact.

## See also

- [Delegation guide](delegation.md) — child job trace inheritance.
- [Sessions guide](sessions.md) — full session lifecycle.
- [Spec §11](../../spec/docs/draft-arcp-1.1.md#11-observability)
