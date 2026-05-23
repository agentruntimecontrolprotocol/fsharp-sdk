# Arcp.Otel

OpenTelemetry hooks for the F# SDK. `Arcp.Otel` ships a shared
`ActivitySource`, canonical span attribute keys, and helpers a runtime
implementer can use to wrap a job in an `Activity`.

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
    let Name : string                // "ARCP"
    let Instance : ActivitySource    // new ActivitySource("ARCP", "1.0.0")
```

Subscribe to this source from your OpenTelemetry SDK setup so spans
emitted by the runtime — and any spans your agent code emits via
`Instance.StartActivity(...)` — get exported through your pipeline:

```fsharp
open OpenTelemetry
open OpenTelemetry.Trace
open ARCP.Otel

let tracerProvider =
    Sdk.CreateTracerProviderBuilder()
        .AddSource(ArcpActivitySource.Name)   // subscribe to "ARCP"
        .AddOtlpExporter()
        .Build()
```

## `ArcpSpanAttributes`

Canonical attribute keys for ARCP-related tags. Use these constants
instead of typing the strings so the names stay aligned with the spec
across versions:

```fsharp
module ArcpSpanAttributes =
    let SessionId         : string   // "arcp.session_id"
    let JobId             : string   // "arcp.job_id"
    let Agent             : string   // "arcp.agent"
    let LeaseCapabilities : string   // "arcp.lease.capabilities"
    let LeaseExpiresAt    : string   // "arcp.lease.expires_at"
    let BudgetRemaining   : string   // "arcp.budget.remaining"
```

## `ArcpOtel` module

Two thin helpers a runtime implementer can call to wrap a job's lifetime
in an `Activity`. They are not invoked automatically by `ArcpServer` —
plug them into your job dispatch loop if you want them.

```fsharp
module ArcpOtel =
    /// Start an `arcp.job` activity tagged with session/job/agent and
    /// the lease shape. Returns `None` if no listener is subscribed.
    val beginJobSpan :
        sessionId: SessionId ->
        jobId: JobId ->
        agent: string ->
        lease: LeaseGrant ->
        constraints: LeaseConstraints option ->
        Activity option

    /// Tag an active span with the remaining budget for a currency.
    /// Key is `arcp.budget.remaining.<currency>`.
    val recordBudgetRemaining :
        activity: Activity ->
        currency: string ->
        remaining: decimal ->
        unit
```

Example: span a job from inside a handler wrapper.

```fsharp
open System.Diagnostics
open ARCP.Otel

server.RegisterAgent("report", fun ctx ->
    task {
        let activity =
            ArcpOtel.beginJobSpan
                ctx.SessionId ctx.JobId "report" ctx.Lease ctx.LeaseConstraints

        try
            use _ = activity |> Option.toObj   // ignore None / disposable null
            do! ctx.EmitStatusAsync("running", None, ctx.CancellationToken)
            // ... agent work ...
            return Json.serializeToElement<bool> true
        finally
            activity |> Option.iter (fun a -> a.Dispose())
    })
```

## Custom spans from agent code

Use `ArcpActivitySource.Instance` directly for sub-spans nested under
the job's activity:

```fsharp
open System.Diagnostics
open ARCP.Otel

server.RegisterAgent("report", fun ctx ->
    task {
        use activity = ArcpActivitySource.Instance.StartActivity("collect-sources")
        activity |> Option.ofObj |> Option.iter (fun a ->
            a.SetTag("source.count", 5) |> ignore)
        // ... do work ...
        return Json.serializeToElement<bool> true
    })
```

## What's not in this package

`Arcp.Otel` does not register middleware, inject W3C trace context into
envelopes, or auto-emit one span per `arcp.send` / `arcp.recv`. ARCP's
envelope carries `trace_id` as a first-class field (spec §11); use it
for log correlation. Distributed trace propagation across processes
needs an application-level convention agreed by both peers and is not
provided by this package today.

## See also

- [Observability guide](../guides/observability.md) — trace_id propagation.
- [Arcp.Runtime reference](Arcp.Runtime.md) — `JobContext` fields wrapped above.
