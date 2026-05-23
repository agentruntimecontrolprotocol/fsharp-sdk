# Job events (§8)

Every signal an agent emits during a job is a `job.event` envelope.
The F# SDK models the body as `JobEventBody`, an exhaustive DU with
ten reserved cases plus the `XVendor` arm for the `x-vendor.*`
extension namespace.

## The reserved kinds

| Kind           | DU case (`JobEventBody.*`)                                                       | Purpose                                    |
| -------------- | -------------------------------------------------------------------------------- | ------------------------------------------ |
| `log`          | `Log(level, message)`                                                            | Plain log line.                            |
| `thought`      | `Thought(text)`                                                                  | Model reasoning / internal monologue.      |
| `status`       | `Status(phase, message?)`                                                        | Lifecycle hint (`running`, `fetching`, …). |
| `progress`     | `Progress(current, total?, units?, message?)`                                    | Structured progress tracker (v1.1).        |
| `tool_call`    | `ToolCall(tool, args, callId)`                                                   | Agent invoked a tool.                      |
| `tool_result`  | `ToolResult(callId, ToolOutcome)`                                                | Outcome for a `tool_call`.                 |
| `metric`       | `Metric(name, value, unit?, dimensions?)`                                        | Numeric measurement.                       |
| `artifact_ref` | `ArtifactRef(uri, contentType, byteSize?, sha256?)`                              | Reference to an artifact.                  |
| `delegate`     | `Delegate(DelegateBody)`                                                         | Child-job declaration (§10).               |
| `result_chunk` | `ResultChunk(resultId, chunkSeq, data, encoding, more)`                          | One chunk of a streamed result (v1.1).     |

`ToolOutcome` is either `Result(JsonElement)` or
`Error(code, message, retryable)`. `artifact_ref` is a reference only —
storage is outside ARCP scope.

## Emitting from an agent

`JobContext` (`ctx`) has one method per kind:

```fsharp
open ARCP.Core
open ARCP.Runtime

server.RegisterAgent("research", fun ctx ->
    task {
        do! ctx.EmitStatusAsync("running", None, ctx.CancellationToken)
        do! ctx.EmitLogAsync(LogLevel.Info, "search start", ctx.CancellationToken)
        do! ctx.EmitThoughtAsync("breaking the query into sub-tasks", ctx.CancellationToken)

        do! ctx.EmitToolCallAsync(
                "web.search",
                Json.serializeToElement<{| q: string |}> {| q = "F# async" |},
                "s1",
                ctx.CancellationToken)

        do! ctx.EmitToolResultAsync(
                "s1",
                ToolOutcome.Result(Json.serializeToElement<{| hits: string list |}> {| hits = ["..."] |}),
                ctx.CancellationToken)

        do! ctx.EmitMetricAsync("tokens.in", 1284m, Some "tokens", None, ctx.CancellationToken)

        do! ctx.EmitArtifactRefAsync(
                "s3://reports/2026-W19.md",
                "text/markdown",
                Some 11482L,
                Some "abc...",
                ctx.CancellationToken)

        return Json.serializeToElement<bool> true
    })
```

## Receiving on the client

Events arrive on `handle.Events` (`IAsyncEnumerable<JobEventBody>`).
The body DU comes through directly; the wire `kind` is recoverable via
`JobEventBody.kind`:

```fsharp
// F#
let enumerator = handle.Events.GetAsyncEnumerator(ct)
let mutable more = true
while more do
    let! has = enumerator.MoveNextAsync().AsTask()
    if not has then more <- false
    else
        match enumerator.Current with
        | JobEventBody.Log(level, msg)            -> printfn "[%A] %s" level msg
        | JobEventBody.Status(phase, _)           -> printfn "status: %s" phase
        | JobEventBody.Metric(name, value, u, _)  -> printfn "metric %s = %M %A" name value u
        | JobEventBody.ArtifactRef(uri, mime, _, _) -> printfn "artifact: %s (%s)" uri mime
        | _ -> ()
```

Or in C#:

```csharp
await foreach (var body in handle.Events.WithCancellation(ct))
{
    Console.WriteLine($"[{ARCP.Core.JobEventBody.kind(body)}]");
}
```

## Sequence numbers (§8.3)

`EventSeq` is **session-scoped**, not job-scoped. One counter spans
every concurrent job in the session:

```
session S, two concurrent jobs A and B:

  A: event_seq = 1
  B: event_seq = 2
  A: event_seq = 3
  A: event_seq = 4
  B: event_seq = 5
```

The counter is strictly monotonic and gap-free. Across resume, replay
starts from `last_event_seq + 1` with no holes — this is what lets a
single ack value carry information about every job in the session.

## Progress events (v1.1, §8.2.1)

`EmitProgressAsync` is a structured tracker built on the `status` kind:

```fsharp
for i in 1 .. urls.Length do
    do! ctx.EmitProgressAsync(
            decimal i,
            Some (decimal urls.Length),
            Some "urls",
            Some (sprintf "processed %s" urls.[i-1]),
            ctx.CancellationToken)
```

The body carries `phase = "progress"` plus `current`, `total`, `units`,
and `message` per the v1.1 progress schema.

## Result streaming (v1.1, §8.4)

For results too large to send in a single `job.result`, use the
chunked-result stream:

```fsharp
server.RegisterAgent("large-result", fun ctx ->
    task {
        let resultId = ctx.BeginStreamingResult()
        for i in 1 .. 100 do
            let chunk = System.Text.Encoding.UTF8.GetBytes(sprintf "chunk-%d\n" i)
            do! ctx.EmitResultChunkAsync(
                    resultId, int64 i,
                    System.ReadOnlyMemory chunk,
                    ChunkEncoding.Utf8,
                    i < 100,           // more = false on the last chunk
                    ctx.CancellationToken)
        // return the result_id so the client can identify the stream
        return Json.serializeToElement<{| resultId: string |}> {| resultId = resultId.Value |}
    })
```

The client's `handle.Result` assembles all `result_chunk` events
automatically before resolving. See the [stream-resume recipe](../../recipes/stream-resume/).

## Tool call lease enforcement

`tool_call` events are gated by the lease's `tool.call` capability.
When the check fails, the runtime emits a `tool_result` event on the
**same job** with `error.code = "PERMISSION_DENIED"` — the job stays
alive and the agent decides whether to recover:

```fsharp
// After emitting a tool_call, iterate events to check for denial
let enumerator = handle.Events.GetAsyncEnumerator(ct)
let mutable more = true
while more do
    let! has = enumerator.MoveNextAsync().AsTask()
    if not has then more <- false
    else
        match enumerator.Current with
        | JobEventBody.ToolResult(callId, ToolOutcome.Error(code, msg, _)) ->
            printfn "tool call %s denied: [%s] %s" callId code msg
        | _ -> ()
```

See [leases guide](leases.md) and the [PERMISSION_DENIED recipe](../../recipes.md#graceful-permission_denied-from-a-tool-call).

## Back-pressure interaction

When the `ack` feature is negotiated, the runtime tracks
`event_seq − last_acked_event_seq`. Once the delta exceeds the back-pressure
threshold, `Emit*Async` calls stall on a per-session semaphore until
the client acknowledges more events.

This throttles agent emission for slow consumers instead of dropping
events or queueing unboundedly. See [sessions guide](sessions.md#ack-and-back-pressure-65).

## Vendor extension events

Kinds outside the reserved eight must use the `x-vendor.<vendor>.<kind>`
namespace:

```fsharp
// Emit a vendor-namespaced event
do! ctx.EmitVendorEventAsync(
        "x-vendor.acme.confidence",
        Json.serializeToElement<{| score: float |}> {| score = 0.87 |},
        ctx.CancellationToken)

// Filter on the client by matching the XVendor case
match enumerator.Current with
| JobEventBody.XVendor("x-vendor.acme.confidence", body) -> printfn "got: %s" (body.GetRawText())
| _ -> ()
```

See [vendor extensions guide](vendor-extensions.md).

## See also

- [Jobs guide](jobs.md) — submitting jobs and reading terminal results.
- [Leases guide](leases.md) — capability enforcement.
- [Spec §8](../../spec/docs/draft-arcp-1.1.md#8-event-layer)
