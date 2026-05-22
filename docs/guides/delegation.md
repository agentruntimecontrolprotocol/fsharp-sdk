# Delegation (§10)

Delegation describes child work from inside a parent job. The parent
emits a `delegate` event with the child job id, target agent, and lease
the child should receive. The child lease must be a subset of the
parent's effective lease.

This SDK exposes the delegate event helper and the lease subset
validator. It does not auto-spawn child jobs from a `delegate` event;
supervising code decides whether and how to submit the child job.

## Parent side

```fsharp
open ARCP.Core
open ARCP.Runtime

server.RegisterAgent("orchestrator", fun ctx ->
    task {
        let childLease =
            Lease.empty
            |> Lease.withCapability Capabilities.NetFetch [ "s3://artifacts/**" ]

        let body: DelegateBody = {
            ChildJobId = (JobId.newId()).Value
            Agent = "pdf-renderer"
            Lease = childLease
            LeaseConstraints = None
        }

        do! ctx.EmitStatusAsync("delegating", Some "starting pdf-renderer", ctx.CancellationToken)
        do! ctx.EmitDelegateAsync(body, ctx.CancellationToken)
        return Json.serializeToElement true
    })
```

The delegate event is emitted on the parent's job stream. Use the
`ChildJobId` value as the correlation id if your application submits a
matching child job.

## Child agent

The child handler is registered like any other agent:

```fsharp
server.RegisterAgent("pdf-renderer", fun ctx ->
    task {
        do! ctx.EmitStatusAsync("rendering", None, ctx.CancellationToken)
        let url = "s3://artifacts/rendered.pdf"
        do! ctx.EmitArtifactRefAsync(url, "application/pdf", None, None, ctx.CancellationToken)
        return Json.serializeToElement {| url = url |}
    })
```

## Subset validation (§10.2)

The child's lease must be a subset of the parent's effective lease. The
runtime uses the same `Lease.isSubset` function internally for subset
checks, and applications can call it before submitting child work.

```fsharp
let parent : LeaseGrant =
    Lease.empty
    |> Lease.withCapability Capabilities.NetFetch [ "s3://artifacts/**"; "https://api.example.com/**" ]
    |> Lease.withCapability Capabilities.ToolCall [ "render.*" ]

let ok : LeaseGrant =
    Lease.empty
    |> Lease.withCapability Capabilities.NetFetch [ "s3://artifacts/2026/**" ]
    |> Lease.withCapability Capabilities.ToolCall [ "render.pdf" ]

let bad : LeaseGrant =
    Lease.empty
    |> Lease.withCapability Capabilities.NetFetch [ "s3://**" ]

let check =
    Lease.isSubset ok parent Map.empty None None
```

If subset validation fails, surface `LEASE_SUBSET_VIOLATION` as a
recoverable parent-job error or event. It is not a session-level error.

## Client side

Delegation events arrive through the normal event stream:

```fsharp
let handle = (client.SubmitAsync(request, ct)).Result
let enumerator = handle.Events.GetAsyncEnumerator(ct)

while (enumerator.MoveNextAsync().AsTask().Result) do
    match enumerator.Current with
    | JobEventBody.Delegate body ->
        printfn "child=%s agent=%s" body.ChildJobId body.Agent
    | other ->
        printfn "kind=%s" (JobEventBody.kind other)
```

If supervising code submits a child job, it receives its own `JobHandle`
and terminal result.

## Trace propagation

Propagate the parent's trace id when your application submits child jobs.
With `Arcp.Otel` wired on both sides, your observability backend can
reconstruct the orchestration tree.

See [observability guide](observability.md).

## Cancellation

Cancelling the parent fires the parent's `CancellationToken`. Child jobs
submitted separately are not automatically cancelled. Track child job ids
and cancel them explicitly if your application wants cascade semantics.

## Idempotency

Use `JobSubmitRequest.IdempotencyKey` on the child submission:

```fsharp
let childRequest = {
    Agent = "pdf-renderer"
    Input = Json.serializeToElement {| source = "# Hello" |}
    LeaseRequest = Some childLease
    LeaseConstraints = None
    IdempotencyKey = Some "parent-job:render-pdf"
    MaxRuntimeSec = None
}
```

## See also

- [Leases guide](leases.md) — subset validation rules.
- [Errors guide](errors.md) — `LEASE_SUBSET_VIOLATION`.
- [Job events guide](job-events.md) — `delegate` event kind.
- [Spec §10](../../spec/docs/draft-arcp-1.1.md#10-delegation)
