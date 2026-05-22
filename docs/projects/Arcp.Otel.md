# Arcp.Otel

OpenTelemetry integration for the F# SDK. `Arcp.Otel` injects W3C
trace context into every envelope and produces `arcp.send` / `arcp.recv`
spans in your tracing backend.

## Installation

```
dotnet add package Arcp.Otel
```

## Namespace

```fsharp
open ARCP.Otel
```

## `ArcpActivitySource`

```fsharp
module ArcpActivitySource =
    val Name     : string       // "ARCP"
    val Instance : ActivitySource  // new ActivitySource("ARCP", "1.0.0")
```

Use `ArcpActivitySource.Instance` from inside an agent to emit child
spans under the job's trace:

```fsharp
open System.Diagnostics
open ARCP.Otel

let source = ArcpActivitySource.Instance

server.RegisterAgent("report", fun ctx ->
    task {
        use activity = source.StartActivity("collect-sources")
        activity |> Option.iter (fun a -> a.SetTag("source.count", 5) |> ignore)
        // …
        return Json.serializeToElement<bool> true
    })
```

## `ArcpSpanAttributes`

Constants for ARCP-specific span attributes:

```fsharp
module ArcpSpanAttributes =
    val SessionId         : string   // "arcp.session_id"
    val JobId             : string   // "arcp.job_id"
    val Agent             : string   // "arcp.agent"
    val LeaseCapabilities : string   // "arcp.lease.capabilities"
    val LeaseExpiresAt    : string   // "arcp.lease.expires_at"
    val BudgetRemaining   : string   // "arcp.budget.remaining"
```

## `ArcpOtel` module

Transport-level tracing wrappers:

```fsharp
module ArcpOtel =
    /// Wrap a client-side transport to inject/extract trace context.
    val withClientTracing : TracerProvider -> ITransport -> ITransport

    /// Wrap a server-side transport to inject/extract trace context.
    val withServerTracing : ITransport -> TracerProvider -> ITransport
```

### Manual setup (non-ASP.NET)

```fsharp
open ARCP.Otel
open OpenTelemetry

// Client side
let tracedTransport =
    transport |> ArcpOtel.withClientTracing tracerProvider

let client = new ArcpClient(tracedTransport)

// Server side
let server =
    new ArcpServer(
        serverOptions,
        fun rawTransport ->
            let tracedTransport = ArcpOtel.withServerTracing rawTransport tracerProvider
            sessionHandler tracedTransport)
```

## ASP.NET Core integration

Use the `Arcp.AspNetCore` extension methods together with the OTel SDK:

```fsharp
// Program.fs
builder.Services.AddArcp()
       .AddArcpTracing()   // adds Arcp.Otel to the pipeline

builder.Services
    .AddOpenTelemetry()
    .WithTracing(fun b ->
        b.AddArcpInstrumentation()  // registers ArcpActivitySource.Instance
         .AddOtlpExporter() |> ignore)
```

## Span shape

Two span types are emitted per envelope:

| Span        | Attributes                                                                    |
| ----------- | ----------------------------------------------------------------------------- |
| `arcp.send` | `arcp.type`, `arcp.id`, `arcp.session_id`, `arcp.job_id?`, `arcp.event_seq?` |
| `arcp.recv` | same                                                                          |

For `job.submit`, `job.accepted`, `job.result`, and `job.error`, the
middleware also attaches the `ArcpSpanAttributes` constants above.

The middleware **does not** emit spans for `session.heartbeat` or
`session.pong` — those are high-frequency keep-alive frames.

## Wire shape

`Arcp.Otel` injects trace context into every outbound envelope:

```json
{
  "arcp": "1.1",
  "id": "01J…",
  "type": "job.submit",
  "trace_id": "0123456789abcdef0123456789abcdef",
  "payload": {},
  "extensions": {
    "x-vendor.opentelemetry.tracecontext": {
      "traceparent": "00-0123…-…",
      "tracestate": "vendor=value"
    }
  }
}
```

On receive, the middleware extracts the context and sets it as the
current `Activity` before dispatching to the handler.

## See also

- [Observability guide](../guides/observability.md) — end-to-end trace propagation.
- [Arcp.Runtime reference](Arcp.Runtime.md) — `ArcpServer`, `JobContext`.
- [Arcp.AspNetCore reference](Arcp.AspNetCore.md) — `AddArcpTracing`, `AddArcpInstrumentation`.
