# Arcp.Client

Client-side ARCP. `Arcp.Client` connects to an ARCP runtime, submits
jobs, streams events, lists jobs, subscribes to in-flight work, and
exposes the `ITransport` abstraction with three bundled implementations.

## Installation

```
dotnet add package Arcp.Client
```

## Namespaces

| Namespace                | Contents                                                          |
| ------------------------ | ----------------------------------------------------------------- |
| `ARCP.Client`            | `ArcpClient`, `JobHandle`, `ArcpClientOptions`, `SessionContext`, `JobSubmitRequest`, `SubscribeOptions`, `ITransport`. |
| `ARCP.Client.Transport`  | `MemoryTransport`, `StdioTransport`, `WebSocketClientTransport`.  |
| `ARCP.Client.Internal`   | `AutoAckOptions` (used in `ArcpClientOptions`); other types here are not public API. |

## `ArcpClientOptions`

```fsharp
type ArcpClientOptions =
    {
        /// Client identity advertised in `session.hello.payload.client`.
        Client       : ClientIdentity
        /// Authentication scheme. `AuthScheme.None` sends `auth.scheme = "none"`.
        Auth         : AuthScheme
        /// Feature flags the client advertises in `session.hello`.
        Features     : Set<string>            // default: Features.All
        /// `TimeProvider` for auto-ack scheduling and client-side timers.
        TimeProvider : TimeProvider
        /// Auto-ack settings. Effective only when `ack` is negotiated.
        AutoAck      : AutoAckOptions
    }

ArcpClientOptions.defaults : ArcpClientOptions   // anonymous auth, every feature flag
```

`AutoAckOptions` lives in `ARCP.Client.Internal` and looks like:

```fsharp
type AutoAckOptions = { EveryEvents: int; Interval: TimeSpan }
AutoAckOptions.defaults  // EveryEvents = 32, Interval = 250 ms
```

## `ArcpClient`

```fsharp
type ArcpClient(transport: ITransport, options: ArcpClientOptions) =
    /// Send `session.hello`, await `session.welcome`, return the
    /// negotiated session.
    member ConnectAsync : ct: CancellationToken -> Task<SessionContext>

    /// Submit a new job. Returns once `job.accepted` arrives.
    member SubmitAsync : request: JobSubmitRequest * ct: CancellationToken -> Task<JobHandle>

    /// Subscribe to an in-flight job started in this or another session.
    member SubscribeAsync :
        jobId: JobId * options: SubscribeOptions * ct: CancellationToken -> Task<JobHandle>

    /// Stop receiving events for a subscribed job.
    member UnsubscribeAsync : jobId: JobId * ct: CancellationToken -> Task

    /// Paginated job listing.
    member ListJobsAsync :
        filter: JobListFilter option *
        limit:  int option *
        cursor: string option *
        ct:     CancellationToken
        -> Task<SessionJobsPayload>

    /// Manually emit `session.ack`. Auto-ack is on by default once
    /// `ack` is negotiated.
    member AckAsync : lastProcessedSeq: int64 * ct: CancellationToken -> Task

    /// Negotiated feature set; empty until `ConnectAsync` resolves.
    member NegotiatedFeatures : Set<string>

    /// Negotiated session context (`Some` after `session.welcome`).
    member Session : SessionContext option

    /// Send `session.bye` and close the transport.
    member CloseAsync : reason: string option * ct: CancellationToken -> Task

    interface IDisposable
```

Job cancellation lives on `JobHandle.CancelAsync` — the client does not
expose a top-level `CancelJobAsync`. There is no separate `ResumeAsync`
either; reconnecting a dropped session by handing the runtime a
`Resume` payload in `session.hello` is wired through the codec but
not yet exposed as a dedicated client method.

## `JobSubmitRequest`

```fsharp
type JobSubmitRequest =
    {
        Agent            : string
        Input            : JsonElement
        LeaseRequest     : LeaseGrant option
        LeaseConstraints : LeaseConstraints option
        IdempotencyKey   : string option
        MaxRuntimeSec    : int option
    }
```

Example:

```fsharp
let request : JobSubmitRequest =
    {
        Agent = "research"
        Input = Json.serializeToElement<{| topic: string |}> {| topic = "F# 9" |}
        LeaseRequest = Some {
            Capabilities = Map.ofList [
                Capabilities.NetFetch, [ "https://**" ]
                Capabilities.ToolCall, [ "search"; "summarize" ]
            ]
        }
        LeaseConstraints = None
        IdempotencyKey   = None
        MaxRuntimeSec    = Some 120
    }

let! handle = client.SubmitAsync(request, ct)
```

## `SubscribeOptions`

```fsharp
type SubscribeOptions = { FromEventSeq: int64 option; History: bool }
SubscribeOptions.defaults  // live-only, no history
```

## `SessionContext`

Returned by `ConnectAsync`:

```fsharp
type SessionContext =
    {
        SessionId            : SessionId
        NegotiatedFeatures   : Set<string>
        HeartbeatIntervalSec : int option
        ResumeToken          : string
        ResumeWindowSec      : int
        AgentInventory       : AgentInventory
    }
```

## `JobHandle`

Returned by `SubmitAsync` and `SubscribeAsync`.

```fsharp
type JobHandle =
    /// Job id assigned by the runtime in `job.accepted`.
    member JobId : JobId

    /// Provisioned credentials returned in `job.accepted`. Subscribers
    /// receive an empty list — credential values are confidential.
    member Credentials : Credential list

    /// Async stream of non-chunk event bodies.
    member Events : IAsyncEnumerable<JobEventBody>

    /// Resolves with the terminal `job.result` payload or an `ARCPError`.
    member Result : Task<Result<JobResultPayload, ARCPError>>

    /// Assembled bytes from a `result_chunk` stream (when complete).
    member TryReadResultBytes : resultId: ResultId -> byte[] option

    /// Send `job.cancel` (submitter only; subscribers get PERMISSION_DENIED).
    member CancelAsync :
        reason: string option * ct: CancellationToken
        -> Task<Result<unit, ARCPError>>
```

### Reading events

```fsharp
// F# — task block
task {
    let enumerator = handle.Events.GetAsyncEnumerator(ct)
    let mutable more = true
    while more do
        let! has = enumerator.MoveNextAsync().AsTask()
        if not has then more <- false
        else printfn "kind=%s" (JobEventBody.kind enumerator.Current)
}
```

C# interop:

```csharp
await foreach (var body in handle.Events.WithCancellation(ct))
{
    Console.WriteLine($"[{JobEventBody.kind(body)}]");
}
```

### Waiting for the terminal result

```fsharp
match! handle.Result with
| Ok payload ->
    match payload.Result with
    | Some inline_ -> printfn "result: %s" (inline_.GetRawText())
    | None         -> printfn "result was streamed via result_chunk"
| Error err ->
    printfn "job failed: %s — %s" (ARCPError.code err) (ARCPError.message err)
```

C# callers can use `Result.unwrapOrThrow` (from `ARCP.Core`) for
exception-based flow:

```csharp
try
{
    var payload = ARCP.Core.Result.unwrapOrThrow(handle.Result.Result);
    Console.WriteLine(payload.Result?.GetRawText() ?? "null");
}
catch (ArcpException ex) when (ex.Retryable)
{
    // retry with backoff
}
```

## Transports

`ARCP.Client.Transport` ships three implementations:

- `MemoryTransport.CreatePair() : ITransport * ITransport` — paired
  in-process loopback for unit tests and samples.
- `StdioTransport.fromConsole () : ITransport` — newline-delimited JSON
  over the process's `stdin`/`stdout`. Used by `arcp serve --stdio`.
- `WebSocketClientTransport(socket, ownsSocket = true)` — wraps an
  open `System.Net.WebSockets.WebSocket`. Use
  `WebSocketClientTransport.connectAsync uri token ct` to open one.

See [Transports guide](../transports.md) for the full surface.

## See also

- [Jobs guide](../guides/jobs.md) — submit/cancel lifecycle.
- [Job events guide](../guides/job-events.md) — every event kind.
- [Resume guide](../guides/resume.md) — `ResumeToken`, replay window.
- [Errors guide](../guides/errors.md) — `ARCPError`, retry guidance.
- [Leases guide](../guides/leases.md) — `LeaseGrant`, `LeaseConstraints`.
