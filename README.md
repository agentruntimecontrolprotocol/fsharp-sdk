# ARCP F# SDK

F# reference implementation of the [Agent Runtime Control Protocol (ARCP)][spec].
A typed, single-binary control plane for AI-agent runtimes that owns the
session, job, event-stream, subscription, and lease machinery so applications
stay out of message-routing.

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![F#](https://img.shields.io/badge/F%23-latest-378BBA?logo=fsharp)
![Protocol](https://img.shields.io/badge/ARCP-1-blue)

## Packages

| Package | Role |
| ------- | ---- |
| `Arcp.Core` | Wire types: `Envelope`, `Message` DU, `ARCPError` DU, `LeaseGrant`, codec. No I/O. |
| `Arcp.Client` | `ArcpClient`; in-memory / stdio / WebSocket transports; chunk assembler; auto-ack. |
| `Arcp.Runtime` | `ArcpServer`; job manager; lease validator; subscription fan-out; expiry watchdog; budget counters. |
| `Arcp.AspNetCore` | `IEndpointRouteBuilder.MapArcp(...)`. |
| `Arcp.Giraffe` | `useArcp` `HttpHandler`. |
| `Arcp.Otel` | OpenTelemetry `ActivitySource` + canonical attribute helpers. |
| `Arcp` | Umbrella that re-exports the curated public surface. |
| `Arcp.Cli` | `arcp` global tool for serving over stdio and submitting jobs. |

## Quickstart

<!-- region quickstart -->
```fsharp
open System.Threading
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport
open ARCP.Runtime

let server =
    ArcpServer({ ArcpServerOptions.defaults with Features = Features.All })

server.RegisterAgent("hello", fun ctx ->
    task {
        do! ctx.EmitLogAsync(LogLevel.Info, "saying hello", ctx.CancellationToken)
        return Json.serializeToElement<string> "Hello, ARCP!"
    })

let clientT, serverT = MemoryTransport.CreatePair()
let _ = server.HandleSessionAsync(serverT, CancellationToken.None)

let client =
    new ArcpClient(
        clientT,
        { ArcpClientOptions.defaults with
            Auth = AuthScheme.Bearer "demo"
            Features = Features.All })

let session = (client.ConnectAsync CancellationToken.None).Result

let request : JobSubmitRequest = {
    Agent = "hello"
    Input = Json.serializeToElement<int> 0
    LeaseRequest = None
    LeaseConstraints = None
    IdempotencyKey = None
    MaxRuntimeSec = None
}
let handle = (client.SubmitAsync(request, CancellationToken.None)).Result
let result = handle.Result.Result
```
<!-- endregion -->

## Feature support

All nine flag-gated features ship by default. See [`CONFORMANCE.md`](./CONFORMANCE.md):

- `heartbeat` — `session.ping` / `session.pong`
- `ack` — `session.ack`; auto-ack scheduler (32 events / 250 ms)
- `list_jobs` — `session.list_jobs` / `session.jobs`
- `subscribe` — `job.subscribe` / `job.subscribed` / `job.unsubscribe`
- `lease_expires_at` — `lease_constraints.expires_at`; per-job `ExpiryWatchdog`
- `cost.budget` — `cost.budget` capability + per-currency counters
- `progress` — `progress` event body
- `result_chunk` — streamed `result_chunk` events + reassembly
- `agent_versions` — `name@version`; rich agent inventory in `session.welcome`

## Samples

Twenty-two runnable F# samples under [`samples/`](./samples) — one per
feature, plus host-integration samples for ASP.NET Core, Giraffe, and
OpenTelemetry. Each sample is a single `Program.fs` paired with a small
shared harness.

```bash
dotnet run --project samples/QuickStart
dotnet run --project samples/SubmitAndStream
dotnet run --project samples/CostBudget
dotnet run --project samples/AgentVersions
dotnet run --project samples/AspNetCore   # listens on http://127.0.0.1:7878/arcp
```

## CLI

```bash
dotnet pack src/Arcp.Cli
dotnet tool install --global --add-source ./artifacts Arcp.Cli
arcp serve --stdio
arcp send  --url ws://localhost:7878/arcp --agent hello --input '{"name":"world"}'
```

## Tests

```bash
dotnet test ARCP.slnx
```

Unit tests (xUnit + FsCheck) cover envelope round-trip, codec dispatch,
lease/glob/budget arithmetic, chunk assembly, and feature-set property
laws. Integration tests boot a paired client + runtime over the in-memory
transport and exercise handshake, job lifecycle, idempotency, subscribe,
list-jobs, lease expiry, and budget exhaustion.

## Architecture

See [`planning/v1.1/04-architecture.md`](./planning/v1.1/04-architecture.md)
for the full design. The high-level shape:

- **Wire envelope**: 8 fields; `arcp = "1"`; `payload : JsonElement` for
  lazy decode; codec uses `FSharp.SystemTextJson` with
  `JsonUnionEncoding.InternalTag` keyed on `type` so the discriminator
  sits at the same level as peer fields.
- **`ARCPError`**: exhaustive 15-case DU; every `match` is compile-checked.
- **`LeaseGrant`**: immutable record `Map<namespace, glob list>`;
  `validateLeaseOp` is stateless and runs glob match → expiry → budget
  in that order.
- **Streams**: public surface is `IAsyncEnumerable<_>` for C# interop;
  `taskSeq { }` survives as an internal authoring tool only.

## Spec

[`spec/docs/draft-arcp-02.1.md`][spec]

[spec]: ../spec/docs/draft-arcp-02.1.md
