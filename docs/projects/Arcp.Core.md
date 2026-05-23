# Arcp.Core

Wire-level primitives shared by every other project in the F# SDK.
`Arcp.Core` has no runtime dependencies and no I/O — it is the common
vocabulary for messages, errors, leases, identifiers, and the JSON
codec.

## Installation

```
dotnet add package Arcp.Core
```

## Namespace

Everything lives in a single namespace:

```fsharp
open ARCP.Core
```

There is no separate `ARCP.Core.Wire` namespace — envelope types, the
codec, and the message DU are all in `ARCP.Core`. The `ITransport`
interface and the bundled transports live in `ARCP.Client` /
`ARCP.Client.Transport`, not here.

## `Envelope`

The eight-field wire envelope (spec §5.1). `Payload` is kept as a
`JsonElement` so unknown `type` values round-trip without forcing a
schema decode.

```fsharp
type Envelope =
    {
        Arcp      : string          // protocol literal, always "1.1"
        Id        : string          // ULID/UUIDv7, unique per envelope
        Type      : string          // discriminator
        SessionId : string option   // set after the handshake
        TraceId   : string option   // optional W3C 32-hex
        JobId     : string option   // set on job-scoped envelopes
        EventSeq  : int64 option    // monotonic on job.event / result / error
        Payload   : JsonElement
    }
```

`Envelope` helpers — all return a new record:

```fsharp
Envelope.create         (envType: string) (payload: JsonElement) : Envelope
Envelope.withSessionId  (sid: SessionId) (env: Envelope)         : Envelope
Envelope.withTraceId    (tid: TraceId) (env: Envelope)           : Envelope
Envelope.withJobId      (jid: JobId) (env: Envelope)             : Envelope
Envelope.withEventSeq   (seq: int64) (env: Envelope)             : Envelope
Envelope.withId         (id: string) (env: Envelope)             : Envelope
```

There is no `extensions` field on the envelope. Vendor data goes in the
payload, or on a new envelope type in the `x-vendor.*` namespace (§15).

## `Message`

Exhaustive DU over every protocol message — eighteen cases mirroring
the eighteen `type` strings the spec defines. The codec round-trips
unknown `type` strings as `ARCPError.InvalidRequest`, so adding a new
type is a compile error until every match arm is updated.

```fsharp
type Message =
    | SessionHello     of SessionHelloPayload
    | SessionWelcome   of SessionWelcomePayload
    | SessionPing      of SessionPingPayload
    | SessionPong      of SessionPongPayload
    | SessionAck       of SessionAckPayload
    | SessionListJobs  of SessionListJobsPayload
    | SessionJobs      of SessionJobsPayload
    | SessionBye       of SessionByePayload
    | SessionError     of SessionErrorPayload
    | JobSubmit        of JobSubmitPayload
    | JobAccepted      of JobAcceptedPayload
    | JobEvent         of JobEventPayload
    | JobResult        of JobResultPayload
    | JobError         of JobErrorPayload
    | JobCancel        of JobCancelPayload
    | JobSubscribe     of JobSubscribePayload
    | JobSubscribed    of JobSubscribedPayload
    | JobUnsubscribe   of JobUnsubscribePayload

Message.typeOf          : Message -> string   // wire `type`
Message.countsInEventSeq : Message -> bool    // ping/pong/ack excluded
```

## `Codec`

```fsharp
Codec.toEnvelope    : Message -> Envelope
Codec.toMessage     : Envelope -> Result<Message, ARCPError>
Codec.writeEnvelope : Envelope -> string
Codec.readEnvelope  : string -> Result<Envelope, ARCPError>
```

The codec routes through `Json.Options`, which configures
`JsonFSharpOptions` with `WithUnionExternalTag()` keyed on `"type"`
and `WithUnionUnwrapRecordCases()` so the discriminator sits at the
same level as peer fields (the wire shape required by spec §5.1).

## `ARCPError`

Discriminated union over the fifteen protocol error codes (spec §12):

```fsharp
type ARCPError =
    | PermissionDenied         of message: string * details: JsonElement option
    | LeaseSubsetViolation     of message: string * details: JsonElement option
    | JobNotFound              of jobId: string
    | DuplicateKey             of key: string
    | AgentNotAvailable        of agent: string
    | AgentVersionNotAvailable of agent: string * version: string
    | Cancelled                of reason: string option
    | Timeout                  of afterSec: int
    | ResumeWindowExpired      of requestedSeq: int64 * windowSec: int
    | HeartbeatLost
    | LeaseExpired             of expiredAt: DateTimeOffset
    | BudgetExhausted          of currency: string
    | InvalidRequest           of message: string * details: JsonElement option
    | Unauthenticated          of message: string
    | InternalError            of message: string
```

Helpers on the module:

```fsharp
ARCPError.code      (ARCPError.HeartbeatLost)         // "HEARTBEAT_LOST"
ARCPError.message   (ARCPError.JobNotFound "j1")      // "Job j1 not found"
ARCPError.retryable (ARCPError.Timeout 60)            // true
ARCPError.retryable (ARCPError.PermissionDenied("", None))   // false
ARCPError.details   (ARCPError.PermissionDenied("", Some d)) // Some d
```

Per spec §12, the three retryable codes are `Timeout`, `HeartbeatLost`,
and `InternalError`.

## `ArcpException`

Exception wrapper for code paths that prefer exceptions over `Result`:

```fsharp
type ArcpException(error: ARCPError, ?inner: exn) =
    inherit Exception(ARCPError.message error)
    member _.Error     : ARCPError
    member _.Code      : string     // wire code
    member _.Retryable : bool

Result.unwrapOrThrow : Result<'T, ARCPError> -> 'T
```

`Result.unwrapOrThrow` is the seam between the internal
`Result<_, ARCPError>` world and the throwing C# surface.

## `LeaseGrant`, `LeaseConstraints`

```fsharp
type LeaseGrant = { Capabilities: Map<string, string list> }
type LeaseConstraints = { ExpiresAt: DateTimeOffset }
```

`cost.budget` patterns encode amounts as `currency:decimal`
(e.g. `"USD:2.50"`).

## `Capabilities` module

String constants for the reserved capability namespaces (§9.2):

```fsharp
Capabilities.FsRead        // "fs.read"
Capabilities.FsWrite       // "fs.write"
Capabilities.NetFetch      // "net.fetch"
Capabilities.ToolCall      // "tool.call"
Capabilities.AgentDelegate // "agent.delegate"
Capabilities.CostBudget    // "cost.budget"
Capabilities.ModelUse      // "model.use"
```

Custom namespaces MUST use `x-vendor.<vendor>.<name>`.

## `Glob` module

```fsharp
Glob.compile : string -> Regex
Glob.isMatch : pattern: string -> target: string -> bool
```

`?` matches one non-slash char; `*` matches any run of non-slash chars;
`**` matches across slashes. Patterns are anchored (`^...$`).

## `Lease` module

```fsharp
Lease.empty            : LeaseGrant
Lease.withCapability   : ns:string -> globs:string list -> lease:LeaseGrant -> LeaseGrant
Lease.matches          : lease:LeaseGrant -> capability:string -> target:string -> bool
Lease.parseBudgetAmount : string -> Result<string * decimal, string>
Lease.initialBudgets   : LeaseGrant -> Map<string, decimal>

Lease.isSubset
    (child: LeaseGrant)
    (parent: LeaseGrant)
    (parentRemainingBudget: Map<string, decimal>)
    (parentExpiresAt: DateTimeOffset option)
    (childExpiresAt: DateTimeOffset option) : Result<unit, ARCPError>

Lease.validateLeaseOp
    (lease: LeaseGrant)
    (constraints: LeaseConstraints option)
    (budgets: Map<string, decimal>)
    (now: DateTimeOffset)
    (capability: string)
    (target: string) : Result<unit, ARCPError>
```

`validateLeaseOp` is stateless — namespace+glob match, then expiry,
then per-currency budget counter; first failure short-circuits.

## `Features` module

Feature flags are advertised as a `Set<string>`. The module exports
the string constants and the canonical set:

```fsharp
Features.Heartbeat              // "heartbeat"
Features.Ack                    // "ack"
Features.ListJobs               // "list_jobs"
Features.Subscribe              // "subscribe"
Features.LeaseExpiresAt         // "lease_expires_at"
Features.CostBudget             // "cost.budget"
Features.Progress               // "progress"
Features.ResultChunk            // "result_chunk"
Features.AgentVersions          // "agent_versions"
Features.ModelUse               // "model.use"
Features.ProvisionedCredentials // "provisioned_credentials"

Features.All       : Set<string>                                // every flag
Features.intersect : Set<string> -> Set<string> -> Set<string>  // negotiation rule
```

There is no `FeatureSet` record — `Features` is just constants plus a
set helper.

## `JobEventBody`

```fsharp
type JobEventBody =
    | Log         of level: LogLevel * message: string
    | Thought     of text: string
    | ToolCall    of tool: string * args: JsonElement * callId: string
    | ToolResult  of callId: string * outcome: ToolOutcome
    | Status      of phase: string * message: string option
    | Metric      of name: string * value: decimal * unit: string option * dimensions: Map<string, string> option
    | ArtifactRef of uri: string * contentType: string * byteSize: int64 option * sha256: string option
    | Delegate    of body: DelegateBody
    | Progress    of current: decimal * total: decimal option * units: string option * message: string option
    | ResultChunk of resultId: string * chunkSeq: int64 * data: string * encoding: ChunkEncoding * more: bool
    | XVendor     of kind: string * body: JsonElement

JobEventBody.kind : JobEventBody -> string
```

Ten reserved cases plus `XVendor` for round-tripping unknown
`x-vendor.*` kinds. See the [job events guide](../guides/job-events.md).

## `ToolOutcome`

```fsharp
type ToolOutcome =
    | Result of value: JsonElement
    | Error  of code: string * message: string * retryable: bool
```

`Error` is a free-form trio of strings/flag, not an `ARCPError`. The
spec error codes are conventional values for the `code` field; nothing
forces them.

## `Json` module

Thin wrappers around `System.Text.Json` configured via `Json.Options`:

```fsharp
Json.Options                                 : JsonSerializerOptions
Json.serialize<'T>         (value: 'T)       : string
Json.serializeToElement<'T>(value: 'T)       : JsonElement
Json.deserialize<'T>       (json: string)    : 'T
Json.deserializeElement<'T>(el: JsonElement) : 'T
Json.parseElement          (json: string)    : JsonElement
Json.nullElement           ()                : JsonElement
```

## Branded identifiers

Each id is a `[<Struct>]` single-case DU. The wire form is read via
`.Value`; construction is via `newId` (ULID prefix) or `ofString` /
`tryOfString`.

```fsharp
type MessageId = MessageId of string
type SessionId = SessionId of string
type JobId     = JobId     of string
type ResultId  = ResultId  of string

JobId.newId ()            // JobId "job_01J..."
JobId.ofString "job_x"    // throws on empty string
JobId.tryOfString "job_x" // Result<JobId, string>
(JobId "x").Value         // "x"
```

`SessionId` minted by `newId` carries a `sess_` prefix; `JobId` carries
`job_`; `ResultId` carries `res_` — matching the spec-canonical id
shapes.

## See also

- [Leases guide](../guides/leases.md) — `LeaseGrant`, `Glob`, `Lease`.
- [Errors guide](../guides/errors.md) — `ARCPError`, `ArcpException`.
- [Job events guide](../guides/job-events.md) — `JobEventBody`.
- [Arcp.Runtime reference](Arcp.Runtime.md) — uses `Arcp.Core` throughout.
