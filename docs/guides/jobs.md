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
    Input = Json.serializeToElement<{| text: string |}> {| text = "…" |}
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

`handle.Result` is a `Task<Result<JsonElement, ARCPError>>`:

```fsharp
match! handle.Result with
| Ok output ->
    let result = Json.deserializeElement<MyResult>(output)
    // success path
| Error err ->
    // err is an ARCPError DU case
    printfn "job failed: %A" err
```

## Streaming events

Events arrive before the terminal result. Subscribe to them with
`handle.Events` (`IAsyncEnumerable<JobEventPayload>`):

```fsharp
// F#
for event in handle.Events.ToBlockingEnumerable() do
    match event.Body with
    | JobEventBody.Log(level, msg) -> printfn "[%A] %s" level msg
    | JobEventBody.Status s -> printfn "status: %s" s
    | _ -> ()

let result = handle.Result.Result
```

Or in C#:

```csharp
await foreach (var e in handle.Events)
{
    Console.WriteLine($"[{e.Kind}] {e.Body}");
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
        do! ctx.EmitStatusAsync("running", ctx.CancellationToken)
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

Cancel an in-flight job by calling `CancelAsync` on the handle (or
using the job ID):

```fsharp
do! handle.CancelAsync(ct)
// — or —
do! client.CancelJobAsync(handle.JobId, ct)
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

With the `list_jobs` feature negotiated:

```fsharp
let! page = client.ListJobsAsync(JobListFilter.All, ct)
for summary in page.Jobs do
    printfn "%s %A" summary.JobId summary.State
```

## See also

- [Job events guide](job-events.md) — all event kinds an agent can emit.
- [Leases guide](leases.md) — capability grants per job.
- [Errors guide](errors.md) — error codes and retry logic.
- [Spec §7](../../spec/docs/draft-arcp-1.1.md#7-job-layer)
