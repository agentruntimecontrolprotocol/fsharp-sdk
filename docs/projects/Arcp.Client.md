# Arcp.Client

Client-side ARCP. `Arcp.Client` connects to an ARCP runtime,
submits jobs, streams events, and reads results.

## Installation

```
dotnet add package Arcp.Client
```

## Namespace

```fsharp
open ARCP.Client
```

## `ArcpClientOptions`

```fsharp
type ArcpClientOptions = {
    /// Bearer token or token factory for session.hello auth.
    Token           : string option
    TokenFactory    : (unit -> Task<string>) option

    /// Feature set to advertise in session.hello.
    Features        : FeatureSet  // default: Features.All

    /// Timeout for the initial session.welcome handshake.
    ConnectTimeoutMs: int         // default: 10_000

    /// Maximum interval between pongs before HeartbeatLost.
    HeartbeatTimeoutMs: int       // default: 30_000
}

ArcpClientOptions.defaults : ArcpClientOptions
```

## `ArcpClient`

```fsharp
type ArcpClient =
    new(transport: ITransport, options: ArcpClientOptions)
    new(transport: ITransport)  // uses ArcpClientOptions.defaults

    /// Performs session.hello / session.welcome handshake.
    member ConnectAsync : CancellationToken -> Task<unit>

    /// Submit a new job. Returns a handle to observe it.
    member SubmitAsync  : JobSubmitRequest -> CancellationToken -> Task<JobHandle>

    /// Attach to a job that may have started before this session.
    member ResumeAsync  : ResumeRequest -> CancellationToken -> Task<JobHandle>

    /// List jobs visible on this session.
    member ListJobsAsync : CancellationToken -> Task<JobSummary list>

    /// Cancel a running job.
    member CancelJobAsync : JobId -> string option -> CancellationToken -> Task<unit>

    /// Subscribe to events from a job started in a prior session.
    member SubscribeAsync : JobId -> CancellationToken -> Task<JobHandle>

    /// Close the session gracefully.
    member CloseAsync : CancellationToken -> Task<unit>
```

## `JobSubmitRequest`

```fsharp
type JobSubmitRequest = {
    Agent           : string
    Input           : JsonElement
    LeaseRequest    : LeaseGrant option
    LeaseConstraints: LeaseConstraints option
    IdempotencyKey  : string option
    MaxRuntimeSec   : int option
    TraceId         : string option
}
```

Example:

```fsharp
let request : JobSubmitRequest = {
    Agent  = "research"
    Input  = Json.serializeToElement<{| topic: string |}> {| topic = "F# 9" |}
    LeaseRequest = Some {
        Capabilities = Map.ofList [
            Capabilities.NetFetch, [ "https://**" ]
            Capabilities.ToolCall, [ "search"; "summarize" ]
        ]
    }
    LeaseConstraints = None
    IdempotencyKey   = None
    MaxRuntimeSec    = Some 120
    TraceId          = None
}

let! handle = client.SubmitAsync(request, ct)
```

## `ResumeRequest`

```fsharp
type ResumeRequest = {
    JobId            : JobId
    LastSeenEventSeq : int64
}
```

```fsharp
let! handle =
    client.ResumeAsync(
        { JobId = jobId; LastSeenEventSeq = lastSeq },
        ct)
```

## `JobHandle`

Returned by `SubmitAsync`, `ResumeAsync`, and `SubscribeAsync`.

```fsharp
type JobHandle =
    member JobId       : JobId
    member LeaseGrant  : LeaseGrant option
    member Credentials : Map<string, string>  // provisioned credentials

    /// Async stream of all events for this job (including child jobs).
    member Events      : IAsyncEnumerable<JobEventPayload>

    /// Completes when the job reaches a terminal state.
    member Result      : Task<Result<JsonElement, ARCPError>>
```

### Reading events

```fsharp
// F# — blocking enumerable (useful in scripts / tests)
for event in handle.Events.ToBlockingEnumerable() do
    printfn "seq=%d kind=%s" event.Seq event.Kind

// F# — async for
let! ct = Async.CancellationToken
let asyncSeq = handle.Events.WithCancellation(ct)
for event in asyncSeq do  // in a task { … } block
    printfn "%A" event.Body
```

C# interop:

```csharp
await foreach (var e in handle.Events)
{
    Console.WriteLine($"[{e.Seq}] {e.Kind}");
}
```

### Waiting for the result

```fsharp
match! handle.Result with
| Ok output ->
    let result = Json.deserializeElement<MyResult>(output)
    // success path
| Error (ARCPError.Timeout _) ->
    // retryable — retry with backoff
| Error (ARCPError.PermissionDenied _) ->
    // request a broader lease
| Error err ->
    printfn "job failed: %A" err
```

C# callers can use `Result.UnwrapOrThrow()`:

```csharp
try
{
    var output = handle.Result.UnwrapOrThrow();
    var result = Json.DeserializeElement<MyResult>(output);
}
catch (ArcpException ex) when (ex.Retryable)
{
    // retry with backoff
}
catch (ArcpException ex)
{
    Console.Error.WriteLine($"[{ex.Code}] {ex.Message}");
}
```

## `JobEventPayload`

```fsharp
type JobEventPayload = {
    JobId    : JobId
    Seq      : int64
    Kind     : string
    Body     : JobEventBody
    RawBody  : JsonElement
}
```

Use `event.Body` for pattern matching; use `event.RawBody` to extract
custom vendor payloads when the kind is not in the core set.

## `JobSummary`

Returned by `ListJobsAsync`:

```fsharp
type JobSummary = {
    JobId     : JobId
    Agent     : string
    State     : string   // "running" | "completed" | "failed" | "cancelled"
    CreatedAt : DateTimeOffset
    UpdatedAt : DateTimeOffset
}
```

## Connecting over WebSockets

Use `Arcp.AspNetCore` (server) or build a transport over
`System.Net.WebSockets.ClientWebSocket`:

```fsharp
open System.Net.WebSockets
open ARCP.Client
open ARCP.AspNetCore

let ws = new ClientWebSocket()
do! ws.ConnectAsync(Uri("wss://example.com/arcp"), ct)
let transport = WebSocketTransport.fromClientSocket ws

let client = new ArcpClient(transport)
do! client.ConnectAsync(ct)
```

## In-process (testing)

```fsharp
open ARCP.Core

let (clientTransport, serverTransport) = MemoryTransport.CreatePair()
let client = new ArcpClient(clientTransport)
// … wire up server with serverTransport …
do! client.ConnectAsync(ct)
```

## See also

- [Jobs guide](../guides/jobs.md) — full submit/resume/cancel lifecycle.
- [Job events guide](../guides/job-events.md) — all event kinds.
- [Resume guide](../guides/resume.md) — resuming across disconnects.
- [Errors guide](../guides/errors.md) — `ARCPError`, retry guidance.
- [Leases guide](../guides/leases.md) — `LeaseGrant`, `LeaseConstraints`.
