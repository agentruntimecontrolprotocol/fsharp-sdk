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
| Envelope `extensions` object keys         | `x-vendor.<vendor>.<key>`    |
| Auth scheme                               | `x-vendor.<vendor>.<scheme>` |

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

On the client, filter by `JobEventBody.kind`:

```fsharp
let enumerator = handle.Events.GetAsyncEnumerator(CancellationToken.None)
while (enumerator.MoveNextAsync().AsTask().Result) do
    match enumerator.Current with
    | JobEventBody.XVendor("x-vendor.acme.confidence", body) ->
        let score = Json.deserializeElement<{| score: float |}>(body).score
        printfn "confidence: %f" score
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

## Envelope extensions

Every envelope carries an optional `extensions` object:

```json
{
  "arcp": "1.1",
  "id": "01J…",
  "type": "job.submit",
  "payload": {},
  "extensions": {
    "x-vendor.opentelemetry.tracecontext": {
      "traceparent": "00-…",
      "tracestate": "vendor=value"
    }
  }
}
```

This is how `Arcp.Otel` propagates W3C trace context (see
[observability guide](observability.md)).

Keys outside `x-vendor.*` in `extensions` are rejected on the wire
with `INVALID_REQUEST`. Future ARCP revisions may add reserved keys to
this object; vendors should never claim unprefixed keys.

## Authoring discipline

- **Pick a vendor segment and stick with it.** Mixing
  `x-vendor.acme.*` and `x-vendor.com.acme.*` forks your own namespace.
- **Document the shape.** Other implementers will round-trip your
  extension and may write their own consumers. Publish the schema.
- **Don't reach back into core.** An extension should not require
  patching the SDK to work — if it does, propose a spec change instead.
- **Mark experimental.** Use `x-vendor.<you>.experimental.*` for things
  you may change; promote out when stable.

## Discovery via `capabilities`

Advertise supported extensions in `ArcpServerOptions.Capabilities` so
clients can opt in before sending the corresponding envelopes:

```fsharp
let serverOptions = {
    ArcpServerOptions.defaults with
        Capabilities = {
            Encodings = [ "json" ]
            Agents = [ "echo" ]
            Extensions = [
                "x-vendor.acme.warmup"
                "x-vendor.acme.confidence"
            ]
        }
}
```

The client can inspect `session.welcome.capabilities.extensions` and
decide whether to send the corresponding envelopes.

## See also

- [Observability guide](observability.md) — `x-vendor.opentelemetry.tracecontext`.
- [Leases guide](leases.md) — custom capability namespaces.
- [Job events guide](job-events.md) — reserved event kinds.
- [Spec §15](../../spec/docs/draft-arcp-1.1.md#15-vendor-extensions)
