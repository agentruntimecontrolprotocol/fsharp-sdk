# Errors (§12)

ARCP defines fifteen error codes. The F# SDK models them as the
`ARCPError` discriminated union in `ARCP.Core`. Errors carry a
structured payload and serialize to the wire identically whether they
surface as `session.error`, `job.error`, or as the `error` body inside
a `tool_result`.

## Codes

| Code                          | DU Case                            | Meaning                                           | Retryable |
| ----------------------------- | ---------------------------------- | ------------------------------------------------- | --------- |
| `INVALID_REQUEST`             | `InvalidRequest(msg, details?)`    | Malformed envelope or arguments.                  | No        |
| `UNAUTHENTICATED`             | `Unauthenticated(msg)`             | Bad or missing bearer token.                      | No        |
| `PERMISSION_DENIED`           | `PermissionDenied(msg, details?)`  | Lease check failed.                               | No        |
| `JOB_NOT_FOUND`               | `JobNotFound(jobId)`               | Unknown `job_id`.                                 | No        |
| `AGENT_NOT_AVAILABLE`         | `AgentNotAvailable(agent)`         | Agent name not registered.                        | No        |
| `AGENT_VERSION_NOT_AVAILABLE` | `AgentVersionNotAvailable(a, v)`   | Pinned version absent (v1.1).                     | No        |
| `CANCELLED`                   | `Cancelled(reason?)`               | Job cancelled via `job.cancel`.                   | No        |
| `TIMEOUT`                     | `Timeout(afterSec)`                | Wall-clock `max_runtime_sec` tripped.             | **Yes**   |
| `INTERNAL_ERROR`              | `InternalError(msg)`               | Unhandled runtime error.                          | **Yes**   |
| `LEASE_SUBSET_VIOLATION`      | `LeaseSubsetViolation(msg, det?)`  | Child lease wider than parent (§10).              | No        |
| `LEASE_EXPIRED`               | `LeaseExpired(expiredAt)`          | `lease_constraints.expires_at` reached (v1.1).    | No        |
| `BUDGET_EXHAUSTED`            | `BudgetExhausted(currency)`        | `cost.budget` depleted (v1.1).                    | No        |
| `RESUME_WINDOW_EXPIRED`       | `ResumeWindowExpired(seq, window)` | Resume past `resume_window_sec`.                  | No        |
| `HEARTBEAT_LOST`              | `HeartbeatLost`                    | Two consecutive missed pongs (v1.1).              | **Yes**   |
| `DUPLICATE_KEY`               | `DuplicateKey(key)`                | Idempotency key collision with conflicting input. | No        |

`ARCPError.retryable` reflects the column above.

## Wire shape

```json
{
  "code": "TIMEOUT",
  "message": "job exceeded max_runtime_sec=60",
  "retryable": true,
  "details": { "after_sec": 60 }
}
```

Every wire emission of an error — `session.error.payload`,
`job.error.payload`, `tool_result.body.error` — uses this shape.
`details` is a free-form JSON object for transport-specific context
(e.g., `{ "capability": "net.fetch", "target": "s3://other/" }` on a
permission denial).

## Throwing from an agent

Throw `ArcpException` (which wraps an `ARCPError`) to produce a
`job.error` with a specific code:

```fsharp
open ARCP.Core

server.RegisterAgent("strict", fun ctx ->
    task {
        let input = Json.deserializeElement<{| allowed: bool; url: string option |}>(ctx.Input)

        if not input.allowed then
            raise (ArcpException(ARCPError.PermissionDenied("input.allowed is false", None)))

        if input.url.IsNone then
            raise (ArcpException(ARCPError.InvalidRequest("url is required", None)))

        // …
        return Json.serializeToElement<bool> true
    })
```

Throwing anything other than `ArcpException` (or an exception wrapping
one) produces `INTERNAL_ERROR` and is logged on the runtime.

To carry structured detail, supply a `JsonElement` in the `details`
position of the DU case:

```fsharp
let details =
    Json.serializeToElement<{| capability: string; target: string |}>
        {| capability = "net.fetch"; target = "s3://other/" |}
raise (ArcpException(ARCPError.PermissionDenied("net.fetch denied", Some details)))
```

## Catching on the client

`handle.Result` is `Task<Result<JsonElement, ARCPError>>`:

```fsharp
match! handle.Result with
| Ok output ->
    let result = Json.deserializeElement<MyResult>(output)
    // success path
| Error (ARCPError.Timeout _) ->
    // retryable — retry with backoff
| Error (ARCPError.InternalError _) ->
    // also retryable
| Error (ARCPError.PermissionDenied _) ->
    // request a broader lease
| Error err ->
    // non-retryable; surface to user
    printfn "job failed: %A" err
```

For C# callers who prefer exceptions, use `Result.unwrapOrThrow`:

```csharp
using ARCP.Core;

try
{
    var output = handle.Result.UnwrapOrThrow(); // throws ArcpException on Error
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

`ArcpException` carries:
- `Error: ARCPError` — the full DU case.
- `Code: string` — the wire code string (e.g., `"TIMEOUT"`).
- `Retryable: bool` — the default retryable flag for this code.

## Session-level errors

`session.error` is fatal — the transport closes after the runtime emits
it. The session's task rejects with the corresponding `ARCPError`.
Common reasons:

- `Unauthenticated` — token failed verification.
- `InvalidRequest` — malformed `session.hello`.
- `ResumeWindowExpired` — resume past the window.

Recovery is always "start a new session." There is no
`session.warning` or recoverable session-level state.

## Errors on a `tool_result`

When an agent's tool call fails for application reasons, encode the
failure via `ToolOutcome.Error` rather than throwing:

```fsharp
do! ctx.EmitToolResultAsync(
        "fetch-1",
        ToolOutcome.Error("INVALID_REQUEST", "404 from upstream", false),
        ctx.CancellationToken)
```

The job stays alive; the agent decides what to do next.

## Lease violations look like `tool_result.error`

When the runtime denies a lease check on `tool_call` or `delegate`, it
emits a `tool_result` on the **parent job** with code
`PERMISSION_DENIED` (or `LEASE_SUBSET_VIOLATION` for delegate subset
failures). This is intentional: the agent decides whether to recover.

```fsharp
// After emitting a tool_call, check for a lease denial
for event in handle.Events.ToBlockingEnumerable() do
    match event.Body with
    | JobEventBody.ToolResult(callId, ToolOutcome.Error err) ->
        printfn "tool call %s denied: %s (retryable=%b)"
            callId (ARCPError.message err) (ARCPError.retryable err)
    | _ -> ()
```

See [leases guide](leases.md) and [delegation guide](delegation.md).

## Retry guidance

`Timeout`, `HeartbeatLost`, and `InternalError` are retryable by
default. Combine retries with idempotency keys (§7.2) so a duplicate
submit collapses to the same `job_id`:

```fsharp
let key = "weekly-report-2026-W20"

let rec trySubmit attempt =
    task {
        let request = {
            Agent = "weekly-report"
            Input = Json.serializeToElement<{| week: string |}> {| week = "2026-W20" |}
            IdempotencyKey = Some key
            LeaseRequest = None
            LeaseConstraints = None
            MaxRuntimeSec = None
        }
        let! handle = client.SubmitAsync(request, ct)
        match! handle.Result with
        | Ok result -> return result
        | Error err when ARCPError.retryable err && attempt < 3 ->
            do! Task.Delay(pown 2 attempt * 1000)
            return! trySubmit (attempt + 1)
        | Error err ->
            raise (ArcpException err)
    }
```

## See also

- [Leases guide](leases.md) — `PERMISSION_DENIED`, `LEASE_SUBSET_VIOLATION`, `BUDGET_EXHAUSTED`.
- [Jobs guide](jobs.md) — idempotency keys, cancellation.
- [Sessions guide](sessions.md) — `session.error` lifecycle.
- [Spec §12](../../spec/docs/draft-arcp-1.1.md#12-error-layer)
