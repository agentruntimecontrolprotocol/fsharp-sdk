# Jobs (§7)

A job is the unit of work in ARCP. The client submits a job, the
runtime dispatches it to a registered agent, and events flow back until
a terminal result or error is emitted.

## Submitting a job

Fill a `JobSubmitRequest` and call `SubmitAsync`:

```fsharp
open ARCP.Core
open ARCP.Client

let request : JobSubmitRequest = {
    Agent = "summarize"
    Input = Json.serializeToElement<{| text: string |}> {| text = "..." |}
    LeaseRequest = None
    LeaseConstraints = None
    IdempotencyKey = None
    MaxRuntimeSec = None
}

let handle = (client.SubmitAsync(request, ct)).Result
```

`SubmitAsync` returns once `job.accepted` arrives. The agent runs
asynchronously; `handle.Result` completes on the terminal event.

## Job lifecycle

Jobs transition through these states:

```
pending → running → success
                  → error
                  → cancelled
                  → timed_out
```

`handle.Result` is a `Task<Result<JobResultPayload, ARCPError>>`:

```fsharp
match! handle.Result with
| Ok payload ->
    match payload.Result with
    | Some inline_ ->
        let result = Json.deserializeElement<MyResult>(inline_)
        // success path
    | None ->
        // result was streamed via result_chunk; assemble via handle.TryReadResultBytes
        ()
| Error err ->
    // err is an ARCPError DU case
    printfn "job failed: %A" err
```

## Streaming events

Events arrive before the terminal result. `handle.Events` is an
`IAsyncEnumerable<JobEventBody>` — the events come through as the body
DU directly (the wire `kind` is recoverable via `JobEventBody.kind`):

```fsharp
// F#
let enumerator = handle.Events.GetAsyncEnumerator(ct)
let mutable more = true
while more do
    let! has = enumerator.MoveNextAsync().AsTask()
    if not has then more <- false
    else
        match enumerator.Current with
        | JobEventBody.Log(level, msg)    -> printfn "[%A] %s" level msg
        | JobEventBody.Status(phase, _)   -> printfn "status: %s" phase
        | _ -> ()

let! result = handle.Result
```

Or in C#:

```csharp
await foreach (var body in handle.Events.WithCancellation(ct))
{
    Console.WriteLine($"[{ARCP.Core.JobEventBody.kind(body)}]");
}
var result = await handle.Result;
```

## Registering an agent

On the server, register an agent with a name and an
`ArcpAgentHandler`:

```fsharp
server.RegisterAgent("summarize", fun ctx ->
    task {
        let input = Json.deserializeElement<{| text: string |}>(ctx.Input)
        do! ctx.EmitStatusAsync("running", None, ctx.CancellationToken)
        let summary = summarize input.text
        return Json.serializeToElement<string> summary
    })
```

`ctx.Input` is the `JsonElement` from the submit request.
The handler returns `Task<JsonElement>`; the runtime wraps it as
`job.result`. Unhandled exceptions become `job.error` with
`INTERNAL_ERROR`.

## Agent versions (v1.1)

Agents can be registered under multiple versions:

```fsharp
// These strings are agent versions. The protocol version remains "1.1".
server.RegisterAgentVersion("summarize", "1.0", handlerV1)
server.RegisterAgentVersion("summarize", "2.0", handlerV2)
server.SetDefaultAgentVersion("summarize", "2.0")
```

Clients can pin a version with `name@version` in the `Agent` field:

```fsharp
let request = { request with Agent = "summarize@1.0" }
```

If the requested version isn't registered, the runtime replies with
`AGENT_VERSION_NOT_AVAILABLE`.

## Idempotency

Pass `IdempotencyKey` in the submit request to make job submission
idempotent. A second submit with the same key collapses to the
original `job_id`:

```fsharp
let request = {
    Agent = "weekly-report"
    Input = Json.serializeToElement<{| week: string |}> {| week = "2026-W20" |}
    IdempotencyKey = Some "weekly-2026-W20"
    LeaseRequest = None
    LeaseConstraints = None
    MaxRuntimeSec = None
}
```

The idempotency cache is scoped to `(principal, key)`. Two different
principals with the same key get independent jobs.

If the same key is submitted with **different input**, the runtime
replies with `DUPLICATE_KEY`.

## Cancellation

Cancel an in-flight job by calling `CancelAsync` on the handle. There
is no top-level `client.CancelJobAsync` — cancellation always flows
through the handle so a subscriber (who lacks cancel authority) can be
rejected cleanly:

```fsharp
let! result = handle.CancelAsync(Some "user requested", ct)
match result with
| Ok ()      -> ()
| Error err  -> printfn "cancel rejected: %s" (ARCPError.code err)
```

Inside the agent, check `ctx.CancellationToken`:

```fsharp
server.RegisterAgent("long-task", fun ctx ->
    task {
        for i in 1..100 do
            ctx.CancellationToken.ThrowIfCancellationRequested()
            do! doWork i
        return Json.serializeToElement<bool> true
    })
```

When the token fires, the agent should clean up and return normally (or
rethrow `OperationCanceledException`); the runtime emits
`job.error` with `CANCELLED`.

## Wall-clock timeout

Set `MaxRuntimeSec` to cap the job's wall-clock budget:

```fsharp
let request = {
    request with
        MaxRuntimeSec = Some 60  // 60 seconds
}
```

On expiry the runtime sends `TIMEOUT` and cancels the agent's token.
`TIMEOUT` is retryable by default — combine it with `IdempotencyKey`
to retry safely.

## Listing jobs

With the `list_jobs` feature negotiated. `ListJobsAsync` takes a
filter, a limit, a cursor, and the cancellation token, and returns the
`SessionJobsPayload`:

```fsharp
let filter : JobListFilter =
    { Status = Some [ JobStatus.Running; JobStatus.Pending ]
      Agent = None
      CreatedAfter = None }

let! page = client.ListJobsAsync(Some filter, Some 20, None, ct)
for summary in page.Jobs do
    printfn "%s %A" summary.JobId summary.Status
match page.NextCursor with
| Some c -> // paginate with client.ListJobsAsync(filter, limit, Some c, ct)
            ()
| None -> ()
```

## See also

- [Job events guide](job-events.md) — all event kinds an agent can emit.
- [Leases guide](leases.md) — capability grants per job.
- [Errors guide](errors.md) — error codes and retry logic.
- [Spec §7](../../spec/docs/draft-arcp-1.1.md#7-job-layer)
