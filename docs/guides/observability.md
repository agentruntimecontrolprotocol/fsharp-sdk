# Observability (§11)

ARCP carries trace correlation via the envelope's first-class
`trace_id` field — a single 32-hex string that flows from
`session.hello` through every envelope on the session. `Arcp.Otel`
ships a shared `ActivitySource` and canonical span attribute keys for
SDKs that want to wrap a job's lifetime in an `Activity`.

## Trace correlation on the wire

Each envelope can carry `trace_id` as a top-level optional field:

```json
{
  "arcp": "1.1",
  "id": "01J...",
  "type": "job.submit",
  "trace_id": "0123456789abcdef0123456789abcdef",
  "payload": { "agent": "echo", "input": {} }
}
```

The runtime preserves the value across the job lifecycle: it shows up
on `job.accepted.payload.trace_id`, on every `job.event`, and on
`job.subscribed.payload.trace_id` when another session attaches. The
F# SDK does not emit a `traceparent`/`tracestate` `extensions` block —
the envelope has no `extensions` field. Distributed propagation needs
an application-level convention agreed by both peers.

## Setup

Subscribe your OpenTelemetry pipeline to the
`ArcpActivitySource.Instance` source:

```fsharp
open System.Diagnostics
open ARCP.Otel
open OpenTelemetry
open OpenTelemetry.Trace

let tracerProvider =
    Sdk.CreateTracerProviderBuilder()
        .AddSource(ArcpActivitySource.Name)   // "ARCP"
        .AddOtlpExporter()
        .Build()
```

## Span attribute keys

Use the constants on `ArcpSpanAttributes` so the spec-canonical names
stay aligned across SDK versions:

```fsharp
ArcpSpanAttributes.SessionId         // "arcp.session_id"
ArcpSpanAttributes.JobId             // "arcp.job_id"
ArcpSpanAttributes.Agent             // "arcp.agent"
ArcpSpanAttributes.LeaseCapabilities // "arcp.lease.capabilities"
ArcpSpanAttributes.LeaseExpiresAt    // "arcp.lease.expires_at"
ArcpSpanAttributes.BudgetRemaining   // "arcp.budget.remaining"
```

## Job spans

`ArcpOtel.beginJobSpan` opens an `arcp.job` activity tagged with the
session, job, agent, lease capabilities, and (when set) the lease
expiry. `recordBudgetRemaining` adds a per-currency tag under
`arcp.budget.remaining.<currency>`:

```fsharp
open System.Diagnostics
open ARCP.Otel

server.RegisterAgent("report", fun ctx ->
    task {
        let activity =
            ArcpOtel.beginJobSpan
                ctx.SessionId ctx.JobId "report" ctx.Lease ctx.LeaseConstraints

        try
            do! ctx.EmitStatusAsync("running", None, ctx.CancellationToken)
            // ... do work ...
            return Json.serializeToElement<bool> true
        finally
            activity |> Option.iter (fun a -> a.Dispose())
    })
```

The runtime does not call `beginJobSpan` for you — it's a helper you
plug into your own handler shell when you want a span per job.

## Per-tool spans

Use `ArcpActivitySource.Instance` for sub-activities under the current
job span. `Activity`/`AsyncLocal` propagates the parent automatically
across `Task` hops:

```fsharp
open System.Diagnostics
open ARCP.Otel

server.RegisterAgent("report", fun ctx ->
    task {
        use activity = ArcpActivitySource.Instance.StartActivity("collect-sources")
        activity
        |> Option.ofObj
        |> Option.iter (fun a -> a.SetTag("source.count", 5) |> ignore)

        do! ctx.EmitStatusAsync("collecting", None, ctx.CancellationToken)
        // ... do work ...
        return Json.serializeToElement<bool> true
    })
```

## Manual `trace_id`

If you don't want to take `Arcp.Otel` as a dependency, set `trace_id`
yourself before sending the envelope. The codec exposes
`Envelope.withTraceId`; the client doesn't expose a public knob on
`JobSubmitRequest` today, so applications that need explicit propagation
typically inject it via a custom transport wrapper that calls
`Envelope.withTraceId` on the outbound envelope.

`trace_id` is just a 32-hex string; you can mint one with
`System.Guid.NewGuid().ToString("N")`. Even without an OTel exporter
it's useful for log correlation.

## Delegation cascades

Children inherit the parent's `trace_id` because the parent runtime
copies it onto the child's submitted envelope. With `Arcp.Otel` wired
on both sides (subscribed source + `beginJobSpan` per job) you get the
orchestration tree in your backend without further plumbing.

## Heartbeats vs spans

`session.ping` / `session.pong` are keep-alive frames, not job work.
Don't span them — they're high-frequency, low-value noise.

## See also

- [Delegation guide](delegation.md) — child job trace inheritance.
- [Sessions guide](sessions.md) — full session lifecycle.
- [Spec §11](../../spec/docs/draft-arcp-1.1.md#11-observability)
