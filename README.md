<h3 align="center">ARCP F# SDK</h3>

<p align="center"><strong>F# SDK for the Agent Runtime Control Protocol (ARCP) — submit, observe, and control long-running agent jobs from F#.</strong></p>

<p align="center">
  <a href="https://www.nuget.org/packages/Arcp"><img alt="NuGet" src="https://img.shields.io/nuget/v/Arcp.svg"></a>
  <a href="https://github.com/agentruntimecontrolprotocol/fsharp-sdk/actions/workflows/test.yml"><img alt="CI" src="https://github.com/agentruntimecontrolprotocol/fsharp-sdk/actions/workflows/test.yml/badge.svg"></a>
  <a href="https://github.com/agentruntimecontrolprotocol/spec/blob/main/docs/draft-arcp-1.1.md"><img alt="ARCP" src="https://img.shields.io/badge/ARCP-v1.1%20draft-blue"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-Apache--2.0-lightgrey"></a>
  <a href="https://coderabbit.ai"><img alt="CodeRabbit" src="https://img.shields.io/coderabbit/prs/github/agentruntimecontrolprotocol/fsharp-sdk?utm_source=oss&utm_medium=github&utm_campaign=agentruntimecontrolprotocol/fsharp-sdk&labelColor=171717&color=FF570A&label=CodeRabbit+Reviews"></a>
</p>

<p align="center">
  <a href="https://github.com/agentruntimecontrolprotocol/spec/blob/main/docs/draft-arcp-1.1.md">Specification</a> ·
  <a href="#concepts">Concepts</a> ·
  <a href="#installation">Install</a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="docs/">Guides</a> ·
  <a href="docs/">API reference</a>
</p>

---

`Arcp` is the F# reference implementation of [ARCP](https://github.com/agentruntimecontrolprotocol/spec/blob/main/docs/draft-arcp-1.1.md), the Agent Runtime Control Protocol. It covers both sides of the wire — `Arcp.Client` for submitting and observing jobs, `Arcp.Runtime` for hosting agents, with `Arcp.AspNetCore` and `Arcp.Giraffe` middleware for in-process hosting — so either side can talk to any conformant peer in any language without hand-rolling the envelope, sequencing, or lease enforcement.

ARCP itself is a transport-agnostic wire protocol for long-running AI agent jobs. It owns the parts of agent infrastructure that don't change between products — sessions, durable event streams, capability leases, budgets, resume — and stays out of the parts that do. ARCP wraps the agent function; it does not define how agents are built, how tools are exposed (that's MCP), or how telemetry is exported (that's OpenTelemetry).

## Installation

Requires the .NET 10 SDK (`net10.0`); the exact pinned SDK version lives in `global.json`. The umbrella `Arcp` package pulls in `Arcp.Core`, `Arcp.Client`, and `Arcp.Runtime`; pick à la carte if you only need one side of the wire, or add `Arcp.AspNetCore` / `Arcp.Giraffe` / `Arcp.Otel` for host integrations and the `Arcp.Cli` global tool for a ready-made `arcp` binary.

```sh
dotnet add package Arcp
# à la carte:
dotnet add package Arcp.Client   # client side
dotnet add package Arcp.Runtime  # runtime side
# host integrations:
dotnet add package Arcp.AspNetCore
dotnet add package Arcp.Giraffe
dotnet add package Arcp.Otel
# CLI:
dotnet tool install --global Arcp.Cli
```

## Quick start

Connect to a runtime, submit a job, stream its events to completion:

```fsharp
open System.Threading
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport

task {
    let! transport =
        WebSocketClientTransport.connectAsync
            (System.Uri "wss://runtime.example.com/arcp")
            (Some (System.Environment.GetEnvironmentVariable "ARCP_TOKEN"))
            CancellationToken.None

    use client =
        new ArcpClient(
            transport,
            { ArcpClientOptions.defaults with
                Auth = AuthScheme.Bearer (System.Environment.GetEnvironmentVariable "ARCP_TOKEN") })

    let! _session = client.ConnectAsync CancellationToken.None

    let! handle =
        client.SubmitAsync(
            { Agent = "data-analyzer"
              Input = Json.serializeToElement {| dataset = "s3://example/sales.csv" |}
              LeaseRequest = Some (Lease.empty |> Lease.withCapability Capabilities.NetFetch [ "s3://example/**" ])
              LeaseConstraints = None
              IdempotencyKey = None
              MaxRuntimeSec = None },
            CancellationToken.None)

    let! result = handle.Result
    match result with
    | Ok r -> printfn "final: %s" (r.Result |> Option.map (fun v -> v.GetRawText()) |> Option.defaultValue "null")
    | Error e -> eprintfn "job failed: %s" (ARCPError.code e)

    do! client.CloseAsync(None, CancellationToken.None)
} |> fun t -> t.GetAwaiter().GetResult()
```

This is the whole shape of the SDK: open a session, submit work, consume an ordered event stream, get a terminal result or error. Everything below is detail on those four moves.

## Concepts

ARCP organizes everything around four concerns — **identity**, **durability**, **authority**, and **observability** — expressed through five core objects:

- **Session** — a connection between a client and a runtime. A session carries identity (a bearer token), negotiates a feature set in a `hello`/`welcome` handshake, and is *resumable*: if the transport drops, you reconnect with a resume token and the runtime replays buffered events. Jobs outlive the session that started them. See [§6](https://github.com/agentruntimecontrolprotocol/spec/blob/main/docs/draft-arcp-1.1.md).
- **Job** — one unit of agent work submitted into a session. A job has an identity, an optional idempotency key, a resolved agent version, and a lifecycle that ends in exactly one terminal state: `success`, `error`, `cancelled`, or `timed_out`. See [§7](https://github.com/agentruntimecontrolprotocol/spec/blob/main/docs/draft-arcp-1.1.md).
- **Event** — the ordered, session-scoped stream a job emits: logs, thoughts, tool calls and results, status, metrics, artifact references, progress, and streamed result chunks. Events carry strictly monotonic sequence numbers so the stream survives reconnects gap-free. See [§8](https://github.com/agentruntimecontrolprotocol/spec/blob/main/docs/draft-arcp-1.1.md).
- **Lease** — the authority a job runs under, expressed as capability grants (`fs.read`, `fs.write`, `net.fetch`, `tool.call`, `agent.delegate`, `cost.budget`, `model.use`). The runtime enforces the lease at every operation boundary; a job can never act outside it. Leases may carry a budget and an expiry, and may be subset and handed to sub-agents via delegation. See [§9](https://github.com/agentruntimecontrolprotocol/spec/blob/main/docs/draft-arcp-1.1.md).
- **Subscription** — read-only attachment to a job started elsewhere (e.g. a dashboard watching a job a CLI submitted). A subscriber observes the live event stream but cannot cancel or mutate the job. Distinct from *resume*, which continues the original session and carries cancel authority. See [§7.6](https://github.com/agentruntimecontrolprotocol/spec/blob/main/docs/draft-arcp-1.1.md).

The SDK models each of these as first-class objects; the rest of this README shows how.

## Guides

### Sessions and resume

Open a session, negotiate features, and reconnect transparently after a transport drop using the resume token — jobs keep running server-side while you're gone.

```fsharp
open System
open System.Threading
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport

task {
    let! transport =
        WebSocketClientTransport.connectAsync
            (Uri "wss://runtime.example.com/arcp")
            (Some "demo-token")
            CancellationToken.None

    let client =
        new ArcpClient(
            transport,
            { ArcpClientOptions.defaults with
                Auth = AuthScheme.Bearer "demo-token" })

    let! session = client.ConnectAsync CancellationToken.None
    let sessionId = session.SessionId
    let resumeToken = session.ResumeToken
    // Track the highest event_seq you've durably processed; in this SDK
    // the auto-ack scheduler captures it on your behalf when `ack` is
    // negotiated, but you can also persist `session.ack`'s argument.

    // ... transport drops ...

    let! transport2 =
        WebSocketClientTransport.connectAsync
            (Uri "wss://runtime.example.com/arcp")
            (Some "demo-token")
            CancellationToken.None

    // The session.hello carries a ResumeRequest; the runtime replays
    // every event with event_seq > LastEventSeq, then resumes streaming.
    // See ResumeRequest in ARCP.Core.Messages for the wire shape.
    return sessionId, resumeToken
} |> ignore
```

### Submitting jobs

Submit a job with an agent (optionally version-pinned as `name@version`), an input, and an optional lease request, idempotency key, and runtime limit.

```fsharp
let! handle =
    client.SubmitAsync(
        { Agent = "weekly-report@2.1.0"
          Input = Json.serializeToElement {| week = "2026-W19" |}
          LeaseRequest =
              Some (Lease.empty
                    |> Lease.withCapability Capabilities.NetFetch [ "s3://reports/**" ])
          LeaseConstraints =
              Some { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes 1.0 }
          IdempotencyKey = Some "weekly-report-2026-W19"
          MaxRuntimeSec = Some 300 },
        CancellationToken.None)

printfn "job_id = %s" handle.JobId.Value
printfn "credentials = %d provisioned" (List.length handle.Credentials)
```

### Consuming events

Iterate the ordered event stream — `log`, `thought`, `tool_call`, `tool_result`, `status`, `metric`, `artifact_ref`, `progress`, `result_chunk` — and optionally acknowledge progress so the runtime can release buffered events early. Auto-ack runs in the background once `ack` is negotiated (32 events / 250 ms windows by default).

```fsharp
let enumerator = handle.Events.GetAsyncEnumerator CancellationToken.None
try
    let mutable more = true
    while more do
        let! has = enumerator.MoveNextAsync().AsTask()
        if not has then
            more <- false
        else
            match enumerator.Current with
            | JobEventBody.Log (level, message) ->
                printfn "[%A] %s" level message
            | JobEventBody.ToolCall (tool, args, _callId) ->
                printfn "-> tool %s %s" tool (args.GetRawText())
            | JobEventBody.Metric (name, value, unit, _) ->
                printfn "metric %s = %O %s" name value (Option.defaultValue "" unit)
            | JobEventBody.Progress (current, total, _, _) ->
                printfn "progress %O / %O" current (Option.defaultValue 0m total)
            | other ->
                printfn "event %s" (JobEventBody.kind other)
finally
    ignore (enumerator.DisposeAsync().AsTask())

// Manual ack is rarely needed:
// do! client.AckAsync(lastSeq, CancellationToken.None)
```

### Leases and budgets

Request capabilities, a budget, and an expiry; read budget-remaining metrics as they arrive; handle the runtime's enforcement decisions.

```fsharp
let lease =
    Lease.empty
    |> Lease.withCapability Capabilities.ToolCall [ "search.*"; "fetch.*" ]
    |> Lease.withCapability Capabilities.CostBudget [ "USD:1.00" ]

let! handle =
    client.SubmitAsync(
        { Agent = "web-research"
          Input = Json.serializeToElement {| iterations = 8; perCallUSD = 0.3 |}
          LeaseRequest = Some lease
          LeaseConstraints =
              Some { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes 10.0 }
          IdempotencyKey = None
          MaxRuntimeSec = None },
        CancellationToken.None)

let watchBudget () =
    task {
        for body in handle.Events do
            match body with
            | JobEventBody.Metric ("cost.budget.remaining", value, unit, _) ->
                printfn "budget remaining: %O %s" value (Option.defaultValue "" unit)
            | _ -> ()
    } |> ignore

let! result = handle.Result
match result with
// BUDGET_EXHAUSTED and LEASE_EXPIRED are never retryable.
| Error (ARCPError.BudgetExhausted currency) ->
    eprintfn "out of %s — resubmit with a fresh budget" currency
| Error e -> eprintfn "job ended: %s" (ARCPError.code e)
| Ok _ -> ()
```

### Subscribing to jobs

Attach read-only to a job submitted elsewhere and observe its live stream (with optional history replay) without cancel authority.

```fsharp
let observer =
    new ArcpClient(
        transport,
        { ArcpClientOptions.defaults with
            Auth = AuthScheme.Bearer "dashboard-token" })

let! _ = observer.ConnectAsync CancellationToken.None
let! listing =
    observer.ListJobsAsync(
        Some { Status = Some [ JobStatus.Running ]; Agent = None; IdempotencyKey = None },
        Some 10,
        None,
        CancellationToken.None)

let firstRunning = listing.Jobs |> List.head
let! sub =
    observer.SubscribeAsync(
        JobId.ofString firstRunning.JobId,
        { SubscribeOptions.defaults with History = true },
        CancellationToken.None)

for body in sub.Events do
    printfn "[%s] %s" (JobEventBody.kind body) (sprintf "%A" body)

// ... later ...
do! observer.UnsubscribeAsync(sub.JobId, CancellationToken.None)
```

### Error handling

Catch the typed error taxonomy and respect the `retryable` flag — `LEASE_EXPIRED` and `BUDGET_EXHAUSTED` are never retryable; a naive retry fails identically.

```fsharp
let! result = handle.Result
match result with
| Ok r -> printfn "ok: %s" (r.Result |> Option.map (fun v -> v.GetRawText()) |> Option.defaultValue "null")
| Error err ->
    match err with
    | ARCPError.LeaseExpired _
    | ARCPError.BudgetExhausted _ ->
        // Never retryable — resubmit with a fresh lease / budget.
        raise (ArcpException err)
    | _ when ARCPError.retryable err ->
        // Safe to retry with backoff (TIMEOUT, HEARTBEAT_LOST, INTERNAL_ERROR).
        eprintfn "transient: %s" (ARCPError.code err)
    | _ ->
        eprintfn "fatal: %s — %s" (ARCPError.code err) (ARCPError.message err)
```

## Feature support

ARCP features this SDK negotiates during the `hello`/`welcome` handshake:

| Feature flag | Status |
|---|---|
| `heartbeat` | Supported |
| `ack` | Supported |
| `list_jobs` | Supported |
| `subscribe` | Supported |
| `lease_expires_at` | Supported |
| `cost.budget` | Supported |
| `model.use` | Supported |
| `provisioned_credentials` | Supported |
| `progress` | Supported |
| `result_chunk` | Supported |
| `agent_versions` | Supported |

## Transport

ARCP is transport-agnostic. This SDK ships a WebSocket transport (default), a newline-delimited JSON stdio transport for in-process child runtimes, and an in-memory loopback transport for tests and same-process samples. WebSocket is the default for networked runtimes; stdio is used for in-process child runtimes. Select one by constructing the corresponding `ITransport` (`WebSocketClientTransport.connectAsync uri token ct`, `new StdioTransport(stdin, stdout, ownsStreams=false)`, `MemoryTransport.CreatePair()`) and passing it to the `ArcpClient` constructor; `Arcp.AspNetCore` exposes `IEndpointRouteBuilder.MapArcp(...)` to attach the runtime-side WebSocket upgrade to Kestrel, and `Arcp.Giraffe` exposes `useArcp` for Giraffe pipelines.

## API reference

Full API reference — every type, method, and event payload — is in [`docs/`](docs/).

## Versioning and compatibility

This SDK speaks **ARCP v1.1 (draft)**. The SDK follows semantic versioning independently of the protocol; the protocol version it negotiates is shown above and in `session.hello`. A runtime advertising a different ARCP MAJOR is not guaranteed compatible. Feature mismatches degrade gracefully: the effective feature set is the intersection of what the client and runtime advertise, and the SDK will not use a feature outside it.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Protocol questions and proposed changes belong in the [spec repository](https://github.com/agentruntimecontrolprotocol/spec); SDK bugs and feature requests belong here.

## License

Apache-2.0 — see [`LICENSE`](LICENSE).
