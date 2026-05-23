# Leases (§9)

A lease is the capability grant for one job. It tells the runtime
what the agent is allowed to do — fetch which URLs, read which files,
call which tools. Leases are **immutable** at submit: the runtime can
narrow but never widen what the client requests.

## Shape

```fsharp
type LeaseGrant = {
    Capabilities: Map<string, string list>   // namespace → glob patterns
}
```

A capability name is a dot-separated namespace string. Reserved namespaces:

| Namespace        | What it gates                                             |
| ---------------- | --------------------------------------------------------- |
| `fs.read`        | Filesystem reads. Pattern is a path glob.                 |
| `fs.write`       | Filesystem writes.                                        |
| `net.fetch`      | Outbound HTTP/S3/etc. Pattern is a URL glob.              |
| `tool.call`      | Tool invocation. Pattern matches against `tool` name.     |
| `agent.delegate` | Spawning child jobs. Pattern matches child agent name.    |
| `cost.budget`    | Budget cap — patterns encode `<currency>:<amount>`.       |
| `model.use`      | Model tier restrictions. Pattern matches the model name.  |

Custom namespaces MUST use `x-vendor.<vendor>.<cap>`:

```fsharp
let vendorLease =
    { Capabilities = Map.ofList [ "x-vendor.acme.kafka.publish", [ "topic-orders-*" ] ] }
```

The `Capabilities` module exports string constants for every reserved name:

```fsharp
open ARCP.Core

Capabilities.FsRead        // "fs.read"
Capabilities.FsWrite       // "fs.write"
Capabilities.NetFetch      // "net.fetch"
Capabilities.ToolCall      // "tool.call"
Capabilities.AgentDelegate // "agent.delegate"
Capabilities.CostBudget    // "cost.budget"
Capabilities.ModelUse      // "model.use"
```

## Submitting with a lease

```fsharp
let request : JobSubmitRequest = {
    Agent = "research"
    Input = Json.serializeToElement<{| topic: string |}> {| topic = "F# 9" |}
    LeaseRequest = Some {
        Capabilities = Map.ofList [
            Capabilities.NetFetch, [ "https://api.example.com/**"; "s3://reports/**" ]
            Capabilities.ToolCall, [ "web.*"; "summarize" ]
        ]
    }
    LeaseConstraints = None
    IdempotencyKey = None
    MaxRuntimeSec = None
}
```

## Glob matching (§9.2)

- `*` matches a single path segment (no slash).
- `**` matches zero or more segments (crosses slashes).
- Matching is **anchored**: the pattern must match the full target, not just a prefix.

Examples:

| Pattern                      | Matches                               | Does not match                     |
| ---------------------------- | ------------------------------------- | ---------------------------------- |
| `https://api.example.com/*`  | `https://api.example.com/v1`          | `https://api.example.com/v1/users` |
| `https://api.example.com/**` | `https://api.example.com/v1/users/42` | `https://other.example.com/`       |
| `s3://reports/**.csv`        | `s3://reports/2026/W19.csv`           | `s3://reports/2026/W19.json`       |
| `render.*`                   | `render.pdf`                          | `render.pdf.highres` (extra seg)   |

Use `Glob.isMatch` to perform the same matching logic yourself:

```fsharp
open ARCP.Core

Glob.isMatch "https://api.example.com/**" "https://api.example.com/v1/users"
// true

Glob.isMatch "render.*" "render.pdf.highres"
// false
```

## Immutability at submit

The runtime may **reduce** the lease (drop a capability or narrow a
pattern) but never widen it. The effective lease is returned on
`job.accepted` and is also reflected on `JobContext.Lease` inside the
agent:

```fsharp
server.RegisterAgent("research", fun ctx ->
    task {
        // Inspect what was actually granted
        let canFetch = ctx.Lease.Capabilities |> Map.containsKey Capabilities.NetFetch
        // ...
        return Json.serializeToElement<bool> true
    })
```

There is no extension, refresh, or revocation verb in ARCP. If an agent
needs more capability mid-job, submit a fresh job with the broader lease.

## Enforcement points

The runtime checks the lease at the moment of operation:

| Event        | Check                                                              |
| ------------ | ------------------------------------------------------------------ |
| `tool_call`  | `tool.call` pattern covers the tool name.                          |
| `delegate`   | Child `lease_request` is a subset of the parent's effective lease. |
| Expiry check | `LeaseConstraints.ExpiresAt` reached.                              |
| Budget check | `cost.budget` remaining ≥ 0.                                       |

When a check fails, the runtime emits a `tool_result` on the job with
`error.code = "PERMISSION_DENIED"` (or `LEASE_SUBSET_VIOLATION` for
delegate subset failures). The agent decides whether to recover or fail.

## Validating inside an agent

`ctx.ValidateOpAsync` lets you run the same check the runtime would run
before you issue a tool call:

```fsharp
server.RegisterAgent("fetcher", fun ctx ->
    task {
        let url = "https://api.example.com/data"

        // check before attempting the fetch
        match! ctx.ValidateOpAsync(Capabilities.NetFetch, url, ctx.CancellationToken)
               |> Task.map (fun _ -> Ok ())
               |> Task.catch id with
        | Ok () -> ()
        | Error ex -> return Json.serializeToElement<{| skipped: bool |}> {| skipped = true |}

        // ...proceed with fetch
        return Json.serializeToElement<bool> true
    })
```

## Subset validation

A lease `A` is a subset of lease `B` if every capability/pattern in `A`
is covered by `B`. The runtime uses `Lease.isSubset` when validating
delegate child leases:

```fsharp
// Parent's effective lease
let parent : LeaseGrant = {
    Capabilities = Map.ofList [
        Capabilities.NetFetch, [ "s3://artifacts/**"; "https://api.example.com/**" ]
        Capabilities.ToolCall, [ "render.*" ]
    ]
}

// ✓ Subset — narrower patterns
let ok : LeaseGrant = {
    Capabilities = Map.ofList [
        Capabilities.NetFetch, [ "s3://artifacts/2026/**" ]
        Capabilities.ToolCall, [ "render.pdf" ]
    ]
}

// ✗ NOT a subset — broader fetch scope
let bad : LeaseGrant = {
    Capabilities = Map.ofList [
        Capabilities.NetFetch, [ "s3://**" ]  // not covered by parent
    ]
}

// ✗ NOT a subset — new capability the parent doesn't have
let bad2 : LeaseGrant = {
    Capabilities = Map.ofList [
        Capabilities.FsWrite, [ "/tmp/**" ]
    ]
}
```

## Expiration (v1.1, §9.5)

`LeaseConstraints.ExpiresAt` sets a wall-clock deadline on the lease:

```fsharp
let request = {
    Agent = "fetcher"
    Input = Json.serializeToElement<{| url: string |}> {| url = "https://..." |}
    LeaseRequest = Some {
        Capabilities = Map.ofList [
            Capabilities.NetFetch, [ "https://**" ]
        ]
    }
    LeaseConstraints = Some {
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1.0)
    }
    IdempotencyKey = None
    MaxRuntimeSec = None
}
```

Once the clock passes `ExpiresAt`, any lease-gated operation returns
`LEASE_EXPIRED`. The job keeps running — the agent decides whether to
abort. Use the `lease_expires_at` feature flag to enable.

## Budgets (v1.1, §9.6)

Tie a cost cap to a job via `cost.budget`:

```fsharp
let request = {
    Agent = "research"
    Input = Json.serializeToElement<{| topic: string |}> {| topic = "F# 9" |}
    LeaseRequest = Some {
        Capabilities = Map.ofList [
            Capabilities.NetFetch, [ "https://**" ]
            Capabilities.CostBudget, [ "USD:2.00"; "tokens:100000" ]
        ]
    }
    LeaseConstraints = None
    IdempotencyKey = None
    MaxRuntimeSec = None
}
```

Agents drive consumption via `ctx.EmitMetricAsync`. When the `unit`
matches a budget currency, the runtime decrements the remaining balance.
Exhaustion yields `BUDGET_EXHAUSTED` on the next lease-gated op.
`ctx.RemainingBudget` exposes the current balances inside the agent:

```fsharp
server.RegisterAgent("research", fun ctx ->
    task {
        let usdLeft = ctx.RemainingBudget |> Map.tryFind "USD" |> Option.defaultValue 0m
        if usdLeft < 0.10m then
            do! ctx.EmitStatusAsync("stopping", Some "budget low", ctx.CancellationToken)
            return Json.serializeToElement<{| stopped: bool |}> {| stopped = true |}

        do! ctx.EmitMetricAsync("tokens", 450m, Some "tokens", None, ctx.CancellationToken)
        // ...
        return Json.serializeToElement<bool> true
    })
```

The runtime also emits a `metric` event with name `budget_remaining`
when consumption crosses 5% deltas (debounced).

## Provisioned credentials

When both `ArcpServerOptions.Provisioner` and
`ArcpServerOptions.CredentialStore` are configured, the runtime calls
the provisioner after the lease is finalized, before `job.accepted` is
sent. The returned credentials are attached to
`job.accepted.payload.credentials` and surfaced on the client-side
`JobHandle.Credentials`.

Credential values are secrets. The SDK does not include credentials in
`JobSummary`, `session.list_jobs`, or `job.subscribed`. On terminal job
states the runtime revokes every tracked credential through the
configured `ICredentialStore`.

## See also

- [Delegation guide](delegation.md) — subset validation for child leases.
- [Errors guide](errors.md) — `PERMISSION_DENIED`, `LEASE_SUBSET_VIOLATION`, `BUDGET_EXHAUSTED`.
- [Job events guide](job-events.md) — `tool_call` lease enforcement.
- [Spec §9](../../spec/docs/draft-arcp-1.1.md#9-lease-layer)
