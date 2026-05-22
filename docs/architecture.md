# Architecture

## Projects

The SDK is organized as eight .NET projects that compose in layers:

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="diagrams/architecture-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="diagrams/architecture-light.svg">
  <img alt="F# SDK architecture diagram" src="diagrams/architecture-light.svg">
</picture>

```
Arcp                 — umbrella; re-exports the curated public surface
├── Arcp.Core        — wire types, codec, no I/O
├── Arcp.Client      — ArcpClient, transports, auto-ack
└── Arcp.Runtime     — ArcpServer, job lifecycle, lease validation
    ├── Arcp.AspNetCore  — MapArcp endpoint extension
    ├── Arcp.Giraffe     — useArcp HttpHandler
    └── Arcp.Otel        — ActivitySource + span attribute constants

Arcp.Cli             — arcp global tool (serve + send)
```

## `Arcp.Core`

The shared kernel — no client/runtime distinction, no I/O. Exports:

- **`Envelope`** — eight-field wire record. `arcp = "1.1"`.
  `Payload : JsonElement` is kept lazy so decoders only pay for what
  they read.
- **`Message` DU** — one case per protocol message type. Every `match`
  is compile-checked; adding a new type is a compile error until all
  match arms are updated.
- **`ARCPError` DU** — exhaustive 15-case discriminated union for all
  spec error codes. No stringly-typed error handling.
- **`LeaseGrant`** — `Map<namespace, glob list>`; stateless
  `validateLeaseOp` runs glob match → expiry → budget in that order.
- **`Features`** — `Set<string>` of feature flag names; `Features.All`
  is the canonical SDK default.
- **Codec** — `Codec.toEnvelope`, `Codec.toMessage`,
  `Codec.readEnvelope`, `Codec.writeEnvelope`. Uses
  `FSharp.SystemTextJson` with `JsonUnionEncoding.InternalTag` so the
  discriminator `type` field sits at the same level as peer fields.

See [Arcp.Core](projects/Arcp.Core.md).

## `Arcp.Client`

A single class `ArcpClient` owns one transport at a time. It drives
the handshake, dispatches inbound envelopes, and exposes:

- `ConnectAsync(ct)` — sends `session.hello`, awaits `session.welcome`,
  returns `SessionContext`.
- `SubmitAsync(request, ct)` — sends `job.submit`, awaits
  `job.accepted`, returns a `JobHandle` whose `.Result` completes on the
  terminal envelope.
- `SubscribeAsync(jobId, opts, ct)` — attaches to an in-progress job
  from another session (requires `subscribe` feature).
- `ListJobsAsync(filter, ct)` — paginated job listing (requires
  `list_jobs` feature).
- `AckAsync(seq, ct)` — explicit ack for back-pressure (requires `ack`
  feature).
- `CloseAsync(ct)` — graceful session teardown.

Transports: `MemoryTransport`, `StdioTransport`,
`WebSocketClientTransport`. See [transports.md](transports.md).

See [Arcp.Client](projects/Arcp.Client.md).

## `Arcp.Runtime`

Two main types: `ArcpServer` and `JobContext`.

`ArcpServer` holds the agent registry and accepts transports:

```fsharp
server.RegisterAgent("hello", handler)
server.RegisterAgentVersion("hello", "2.0", handlerV2)
server.SetDefaultAgentVersion("hello", "2.0")
server.HandleSessionAsync(transport, ct) // runs one session
```

`JobContext` is the agent's window into the runtime. It emits all
eight reserved event kinds (`EmitLogAsync`, `EmitThoughtAsync`,
`EmitToolCallAsync`, `EmitToolResultAsync`, `EmitStatusAsync`,
`EmitProgressAsync`, `EmitMetricAsync`, `EmitArtifactRefAsync`),
exposes `Lease`, `LeaseConstraints`, and `CancellationToken`, and
provides v1.1 streaming via `BeginStreamingResult()` /
`EmitResultChunkAsync`.

`type ArcpAgentHandler = JobContext -> Task<JsonElement>`

See [Arcp.Runtime](projects/Arcp.Runtime.md).

## `Arcp.AspNetCore` / `Arcp.Giraffe`

Thin adapters that accept a WebSocket upgrade and pass the resulting
`ITransport` to `ArcpServer.HandleSessionAsync`. The server never
knows which web framework it's running in.

See [Arcp.AspNetCore](projects/Arcp.AspNetCore.md) and
[Arcp.Giraffe](projects/Arcp.Giraffe.md).

## `Arcp.Otel`

Provides `ArcpActivitySource.Instance` (the shared `ActivitySource`)
and `ArcpSpanAttributes` constants (`arcp.session_id`, `arcp.job_id`,
`arcp.agent`, `arcp.lease.capabilities`, `arcp.lease.expires_at`,
`arcp.budget.remaining`). Your instrumentation code uses these
constants to attach well-known attributes to spans.

See [Arcp.Otel](projects/Arcp.Otel.md).

## Wire format

Every message is one JSON object with eight top-level fields:

| Field        | Required                                    | Notes                                        |
| ------------ | ------------------------------------------- | -------------------------------------------- |
| `arcp`       | always                                      | `"1.1"` (the protocol version literal)       |
| `id`         | always                                      | ULID or UUIDv7, unique per message           |
| `type`       | always                                      | discriminator (`"session.hello"`, `"job.submit"`, …) |
| `payload`    | always                                      | type-specific body; decoded lazily           |
| `session_id` | after handshake                             | absent on pre-session frames                 |
| `job_id`     | job-scoped envelopes                        | set on `job.*` types                         |
| `event_seq`  | `job.event` / `job.result` / `job.error`    | strictly monotonic per session               |
| `trace_id`   | optional                                    | W3C 32-hex string                            |

Anything else on the wire is ignored. Unknown `x-vendor.*` types are
round-tripped intact per spec §15.

## Design principles

- **Exhaustive DUs, not strings** — `Message`, `ARCPError`, `JobState`,
  `LeaseGrant` are discriminated unions; the F# compiler enforces
  totality.
- **No I/O in `Arcp.Core`** — the wire layer and the network layer
  are strictly separated.
- **`IAsyncEnumerable<_>` on the public surface** — for C# interop;
  `taskSeq { }` is an internal authoring tool only.
- **Stateless lease validation** — `validateLeaseOp` is a pure
  function; the runtime composes it with expiry and budget counters.

## Spec

[`spec/docs/draft-arcp-1.1.md`](../../spec/docs/draft-arcp-1.1.md)
