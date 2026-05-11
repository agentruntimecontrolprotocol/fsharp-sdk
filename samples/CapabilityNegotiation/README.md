# CapabilityNegotiation

LiteLLM-style router selecting peer model-serving runtimes by their
advertised capabilities (cost, latency, model class), with ordered
fallback on `RESOURCE_EXHAUSTED` / `UNAVAILABLE`. Cost rolls up via
the standard `cost.usd` metric.

## Before ARCP

Static `model -> provider` map in YAML. Cost / latency tracked in a
separate per-provider sidecar dashboard. The router has no idea which
provider 429s right now, so retries spray across already-saturated
endpoints. Per-tenant cost reporting requires a third pipeline.

## With ARCP

```fsharp
let profile = profileFrom client.NegotiatedCapabilities  // cost / latency / class at session open
let chain = candidateChain profiles "balanced"
let! reply = invokeWithFallback clients chain "chat.completion" arguments traceId
```

Peer selection is data-driven from the negotiated capabilities — no
sidecar config. Per-call cost lands on the meter via `cost.usd`
metrics, keyed by tenant + peer.

## ARCP primitives

- Capability extensions on the session — RFC §7, §21.
- `tool.invoke` / `tool.error` — §6.3.
- Canonical retry classification — §18.3.
- Standard metric names (`tokens.used`, `cost.usd`) — §17.3.1.
- Envelope `Extensions` for per-call routing context — §6.1.

## File tour

- `Program.fs` — open all peers, route one request, print rollup.
  `Profile`, `candidateChain`, `invokeWithFallback`, `consumeMetric`.

## Variations

- Push fallback chains to a remote policy service.
- Add a `quality` axis (offline eval scores) and weight selection.
- Re-emit the chosen peer + cost back to a billing topic for
  per-call chargeback.
