# Arcp.Runtime

Server-side ARCP. `Arcp.Runtime` hosts sessions, dispatches jobs to
registered agents, enforces leases, manages budgets, and revokes
provisioned credentials when jobs finish.

## Installation

```
dotnet add package Arcp.Runtime
```

## Namespace

```fsharp
open ARCP.Core
open ARCP.Runtime
```

## `ArcpServerOptions`

```fsharp
type ArcpServerOptions = {
    Runtime              : RuntimeIdentity
    Features             : Set<string>
    HeartbeatIntervalSec : int
    ResumeWindowSec      : int
    BearerVerifier       : IBearerVerifier
    TimeProvider         : TimeProvider
    Provisioner          : ICredentialProvisioner option
    CredentialStore      : ICredentialStore option
}

ArcpServerOptions.defaults : ArcpServerOptions
```

If `Provisioner` is set, `CredentialStore` must also be set so issued
credentials can be tracked for revocation.

## `ArcpServer`

```fsharp
type ArcpServer(options: ArcpServerOptions) =
    member RegisterAgent : name: string * handler: ArcpAgentHandler -> unit
    member RegisterAgentVersion : name: string * version: string * handler: ArcpAgentHandler -> unit
    member SetDefaultAgentVersion : name: string * version: string -> unit
    member HandleSessionAsync : transport: ITransport * ct: CancellationToken -> Task
```

### Quick start

```fsharp
let server = ArcpServer(ArcpServerOptions.defaults)

server.RegisterAgent("echo", fun ctx ->
    task {
        do! ctx.EmitStatusAsync("running", Some "echo", ctx.CancellationToken)
        return Json.serializeToElement "ok"
    })

do! server.HandleSessionAsync(transport, ct)
```

## `ArcpAgentHandler`

```fsharp
type ArcpAgentHandler = JobContext -> Task<JsonElement>
```

The return value becomes the inline `result` in `job.result`. Throw
`ArcpException` to produce a `job.error` with a specific protocol code;
other exceptions produce `INTERNAL_ERROR`.

## `JobContext`

Handed to every agent handler. All `Emit*Async` calls write `job.event`
frames on the wire.

```fsharp
type JobContext =
    member JobId : JobId
    member SessionId : SessionId
    member ParentJobId : JobId option
    member Input : JsonElement
    member Lease : LeaseGrant
    member LeaseConstraints : LeaseConstraints option
    member Credentials : Credential list
    member RemainingBudget : Map<string, decimal>
    member CancellationToken : CancellationToken

    member EmitLogAsync : LogLevel * string * CancellationToken -> Task
    member EmitThoughtAsync : string * CancellationToken -> Task
    member EmitToolCallAsync : string * JsonElement * string * CancellationToken -> Task
    member EmitToolResultAsync : string * ToolOutcome * CancellationToken -> Task
    member EmitStatusAsync : string * string option * CancellationToken -> Task
    member EmitProgressAsync : decimal * decimal option * string option * string option * CancellationToken -> Task
    member EmitMetricAsync : string * decimal * string option * Map<string, string> option * CancellationToken -> Task
    member EmitArtifactRefAsync : string * string * int64 option * string option * CancellationToken -> Task
    member EmitDelegateAsync : DelegateBody * CancellationToken -> Task
    member EmitVendorEventAsync : string * JsonElement * CancellationToken -> Task
    member RotateCredentialAsync : string * string * CancellationToken -> Task

    member BeginStreamingResult : unit -> ResultId
    member EmitResultChunkAsync : ResultId * int64 * ReadOnlyMemory<byte> * ChunkEncoding * bool * CancellationToken -> Task
    member ValidateOpAsync : string * string * CancellationToken -> Task
```

### Tool call denial

```fsharp
server.RegisterAgent("assistant", fun ctx ->
    task {
        let callId = "fetch-1"
        do! ctx.EmitToolCallAsync("web.search", Json.serializeToElement {| query = "F# 9" |}, callId, ctx.CancellationToken)
        try
            do! ctx.ValidateOpAsync(Capabilities.ToolCall, "web.search", ctx.CancellationToken)
            return Json.serializeToElement {| ok = true |}
        with
        | :? ArcpException as ex ->
            do! ctx.EmitToolResultAsync(callId, ToolOutcome.Error(ex.Code, ex.Message, ex.Retryable), ctx.CancellationToken)
            return Json.serializeToElement {| ok = false |}
    })
```

### Vendor event

```fsharp
server.RegisterAgent("scorer", fun ctx ->
    task {
        do! ctx.EmitVendorEventAsync(
                "x-vendor.acme.confidence",
                Json.serializeToElement {| score = 0.91 |},
                ctx.CancellationToken)
        return Json.serializeToElement true
    })
```

### Streaming result

```fsharp
server.RegisterAgent("chunked", fun ctx ->
    task {
        let rid = ctx.BeginStreamingResult()
        for i in 0L .. 2L do
            let bytes = Text.Encoding.UTF8.GetBytes(sprintf "chunk %d" i)
            do! ctx.EmitResultChunkAsync(rid, i, ReadOnlyMemory bytes, ChunkEncoding.Utf8, i < 2L, ctx.CancellationToken)
        return Json.serializeToElement<obj> null
    })
```

## Credentials

```fsharp
type CredentialIssueContext = {
    JobId : JobId
    Principal : IPrincipal
    Lease : LeaseGrant
    LeaseConstraints : LeaseConstraints option
    ParentJobId : JobId option
}

type ICredentialProvisioner =
    abstract IssueAsync : CredentialIssueContext * CancellationToken -> Task<Credential list>
    abstract RevokeAsync : credentialId: string * CancellationToken -> Task<bool>

type ICredentialStore =
    abstract RecordIssuedAsync : JobId * Credential -> Task
    abstract RecordRevokedAsync : JobId * credentialId: string -> Task
    abstract ListOutstandingAsync : unit -> Task<(JobId * string) list>
```

## See also

- [Jobs guide](../guides/jobs.md) — full job lifecycle.
- [Job events guide](../guides/job-events.md) — every event kind.
- [Leases guide](../guides/leases.md) — `ctx.Lease`, `ctx.ValidateOpAsync`.
- [Delegation guide](../guides/delegation.md) — `ctx.EmitDelegateAsync`.
- [Vendor extensions guide](../guides/vendor-extensions.md) — `ctx.EmitVendorEventAsync`.
- [Arcp.AspNetCore reference](Arcp.AspNetCore.md) — hosting over HTTP/WebSocket.
