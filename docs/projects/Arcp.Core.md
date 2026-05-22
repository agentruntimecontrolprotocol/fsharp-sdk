# Arcp.Core

Wire-level primitives shared by every other project in the F# SDK.
`Arcp.Core` has no runtime dependencies — it is the common vocabulary
for messages, errors, leases, and identifiers.

## Installation

```
dotnet add package Arcp.Core
```

## Namespaces

| Namespace         | Contents                                                                  |
| ----------------- | ------------------------------------------------------------------------- |
| `ARCP.Core`       | `ARCPError`, `ArcpException`, `LeaseGrant`, `LeaseConstraints`, `Features` |
| `ARCP.Core.Wire`  | Envelope types, `JobEventBody`, `ToolOutcome`, codec helpers              |

## `ARCPError`

Discriminated union of all fifteen protocol error codes:

```fsharp
type ARCPError =
    | InvalidRequest             of message: string * details: JsonElement option
    | Unauthenticated            of message: string
    | PermissionDenied           of message: string * details: JsonElement option
    | JobNotFound                of jobId: string
    | AgentNotAvailable          of agent: string
    | AgentVersionNotAvailable   of agent: string * version: string
    | Cancelled                  of reason: string option
    | Timeout                    of afterSec: int
    | InternalError              of message: string
    | LeaseSubsetViolation       of message: string * details: JsonElement option
    | LeaseExpired               of expiredAt: DateTimeOffset
    | BudgetExhausted            of currency: string
    | ResumeWindowExpired        of requestedSeq: int64 * windowSec: int
    | HeartbeatLost
    | DuplicateKey               of key: string
```

Helper functions on the module:

```fsharp
ARCPError.retryable (ARCPError.Timeout 60)       // true
ARCPError.retryable (ARCPError.PermissionDenied("", None))  // false
ARCPError.message   (ARCPError.JobNotFound "j1") // "j1"
ARCPError.code      (ARCPError.HeartbeatLost)    // "HEARTBEAT_LOST"
```

## `ArcpException`

Exception wrapper for code that prefers exceptions over `Result`:

```fsharp
type ArcpException(error: ARCPError) =
    inherit Exception(ARCPError.message error)
    member _.Error     : ARCPError  // the full DU case
    member _.Code      : string     // wire code, e.g. "TIMEOUT"
    member _.Retryable : bool       // default retryable flag
```

Throw from an agent handler:

```fsharp
raise (ArcpException(ARCPError.PermissionDenied("not allowed", None)))
```

## `LeaseGrant`

```fsharp
type LeaseGrant = {
    Capabilities: Map<string, string list>   // namespace → glob patterns
}
```

## `LeaseConstraints`

```fsharp
type LeaseConstraints = {
    ExpiresAt: DateTimeOffset
}
```

## `Capabilities` module

String constants for reserved capability namespaces:

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

Custom namespaces must use `x-vendor.<vendor>.<name>`.

## `Glob` module

```fsharp
Glob.isMatch (pattern: string) (target: string) : bool
```

Implements the §9.2 matching rules: `*` = single segment, `**` = zero or
more segments, anchored. Shared with the runtime lease validator.

## `Lease` module

```fsharp
// True when every capability/pattern in `child` is covered by `parent`.
Lease.isSubset
    (child: LeaseGrant)
    (parent: LeaseGrant)
    (remainingBudget: Map<string, decimal>)
    (parentExpiresAt: DateTimeOffset option)
    (childExpiresAt: DateTimeOffset option) : bool

// True when `capability`/`target` passes for the given lease state.
Lease.validateLeaseOp
    (lease: LeaseGrant)
    (constraints: LeaseConstraints option)
    (budgets: Map<string, decimal>)
    (now: DateTimeOffset)
    (capability: string)
    (target: string) : Result<unit, ARCPError>
```

## `Features` module

Feature flag set that enables optional protocol features:

```fsharp
type FeatureSet = {
    Heartbeat              : bool
    Ack                    : bool
    ListJobs               : bool
    Subscribe              : bool
    LeaseExpiresAt         : bool
    CostBudget             : bool
    Progress               : bool
    ResultChunk            : bool
    AgentVersions          : bool
    ModelUse               : bool
    ProvisionedCredentials : bool
}

Features.All     : FeatureSet   // all flags true (SDK default)
Features.None    : FeatureSet   // all flags false
```

## `JobEventBody` union

All event body variants the F# SDK can encode or decode:

```fsharp
type JobEventBody =
    | Log        of level: LogLevel * message: string
    | Thought    of text: string
    | Status     of phase: string * message: string option
    | Progress   of current: decimal * total: decimal option * units: string option * message: string option
    | ToolCall   of tool: string * args: JsonElement * callId: string
    | ToolResult of callId: string * outcome: ToolOutcome
    | Metric     of name: string * value: decimal * unit: string option * dimensions: Map<string, string> option
    | ArtifactRef of uri: string * contentType: string * byteSize: int64 option * sha256: string option
    | Delegate   of body: DelegateBody
    | ResultChunk of resultId: string * chunkSeq: int64 * data: string * encoding: ChunkEncoding * more: bool
    | XVendor    of kind: string * body: JsonElement
```

## `ToolOutcome` union

```fsharp
type ToolOutcome =
    | Result of value: JsonElement
    | Error  of code: string * message: string * retryable: bool
```

## `Json` module

Thin wrappers over `System.Text.Json` used throughout the SDK:

```fsharp
Json.serializeToElement<'T> (value: 'T) : JsonElement
Json.deserializeElement<'T> (el: JsonElement) : 'T
Json.roundTrip (el: JsonElement) : JsonElement   // deep-copy
```

## Branded identifiers

```fsharp
type JobId     = JobId of string
type SessionId = SessionId of string

// Helpers
JobId.unwrap    (JobId s)     = s
SessionId.unwrap (SessionId s) = s
```

## Transport interface

```fsharp
type ITransport =
    abstract SendAsync    : Envelope -> CancellationToken -> Task
    abstract ReceiveAsync : CancellationToken -> Task<Envelope option>
    abstract CloseAsync   : CancellationToken -> Task
```

The in-memory transport for unit tests:

```fsharp
open ARCP.Core

let (clientSide, serverSide) = MemoryTransport.CreatePair()
```

## See also

- [Leases guide](../guides/leases.md) — `LeaseGrant`, `Glob`, `Lease`.
- [Errors guide](../guides/errors.md) — `ARCPError`, `ArcpException`.
- [Job events guide](../guides/job-events.md) — `JobEventBody`.
- [Arcp.Runtime reference](Arcp.Runtime.md) — uses `Arcp.Core` throughout.
