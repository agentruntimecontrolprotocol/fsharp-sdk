# Vendor extensions (§15)

ARCP reserves the protocol surface (message types, event kinds,
capability namespaces) but provides a single, well-defined extension
namespace: `x-vendor.<vendor>.<rest>`. Anything in this namespace is
opaque to the runtime — round-tripped intact, ignored when not
understood, never silently dropped.

## What's extensible

| Surface                                   | Vendor namespace             |
| ----------------------------------------- | ---------------------------- |
| Envelope `type`                           | `x-vendor.<vendor>.<type>`   |
| Event `kind` (inside `job.event.payload`) | `x-vendor.<vendor>.<kind>`   |
| Lease capability namespace                | `x-vendor.<vendor>.<cap>`    |
| Auth scheme                               | `x-vendor.<vendor>.<scheme>` |

The F# SDK's `Envelope` record has eight top-level fields and no
`extensions` block; vendor data rides inside a per-message payload or
inside an `XVendor` event body.

## Naming rules

- Must start with `x-vendor.`.
- The vendor segment is a single dot-separated identifier (typically a
  reverse-DNS prefix or a short brand).
- Following segments name the specific extension.
- ASCII letters, digits, `-`, `.`; lower-case by convention.

Examples:

```
x-vendor.acme.cancel
x-vendor.com.example.confidence
x-vendor.opentelemetry.tracecontext
```

## Round-trip guarantee (§15)

The runtime and SDK MUST round-trip unknown `x-vendor.*` types and keys
without modification. The client receives them; if no handler is
registered, they're dropped on the floor at the receiver — but they
were not stripped on the way through.

This means a third-party tool can pass extension metadata through an
arbitrary ARCP runtime without the runtime understanding it.

## Custom event kinds

Use `ctx.EmitVendorEventAsync` to emit a vendor-namespaced event:

```fsharp
open ARCP.Core
open ARCP.Runtime

do! ctx.EmitVendorEventAsync(
        "x-vendor.acme.confidence",
        Json.serializeToElement<{| score: float |}> {| score = 0.87 |},
        ctx.CancellationToken)
```

On the client, filter on the `XVendor` arm of `JobEventBody`:

```fsharp
let enumerator = handle.Events.GetAsyncEnumerator(ct)
let mutable more = true
while more do
    let! has = enumerator.MoveNextAsync().AsTask()
    if not has then more <- false
    else
        match enumerator.Current with
        | JobEventBody.XVendor("x-vendor.acme.confidence", body) ->
            let parsed = Json.deserializeElement<{| score: float |}>(body)
            printfn "confidence: %f" parsed.score
        | _ -> ()
```

## Custom envelope types

The codec preserves unknown `x-vendor.*` envelope types as raw
envelopes. Runtime applications should handle custom envelope types at
their transport boundary before dispatching the core protocol messages.

## Custom lease capabilities

```fsharp
let lease : LeaseGrant = {
    Capabilities = Map.ofList [
        Capabilities.NetFetch, [ "https://**" ]
        "x-vendor.acme.kafka.publish", [ "topic-orders-*"; "topic-payments-*" ]
    ]
}
```

The runtime's lease matcher treats unknown namespaces as opaque —
patterns are matched against whatever the application supplies as the
target. You are responsible for calling `ctx.ValidateOpAsync` from
inside your custom tool wrappers if you want runtime enforcement:

```fsharp
do! ctx.ValidateOpAsync(
        "x-vendor.acme.kafka.publish",
        "topic-orders-new",
        ctx.CancellationToken)
```

## Trace correlation

The envelope's first-class `trace_id` field is the canonical way to
correlate work across processes — no extensions block is needed. See
the [observability guide](observability.md).

## Authoring discipline

- **Pick a vendor segment and stick with it.** Mixing
  `x-vendor.acme.*` and `x-vendor.com.acme.*` forks your own namespace.
- **Document the shape.** Other implementers will round-trip your
  extension and may write their own consumers. Publish the schema.
- **Don't reach back into core.** An extension should not require
  patching the SDK to work — if it does, propose a spec change instead.
- **Mark experimental.** Use `x-vendor.<you>.experimental.*` for things
  you may change; promote out when stable.

## Discovery

The current `WelcomeCapabilities` shape advertises `Encodings`,
`Features`, and `Agents` — there is no dedicated `Extensions` field
yet. Vendors that need explicit discovery can:

1. Inspect `SessionContext.NegotiatedFeatures` for vendor-specific
   feature flags (which must use the `x-vendor.*` form).
2. Send a `x-vendor.<vendor>.*` envelope and observe whether the peer
   echoes it through (round-trip is mandated by §15).

## See also

- [Observability guide](observability.md) — `x-vendor.opentelemetry.tracecontext`.
- [Leases guide](leases.md) — custom capability namespaces.
- [Job events guide](job-events.md) — reserved event kinds.
- [Spec §15](../../spec/docs/draft-arcp-1.1.md#15-vendor-extensions)
