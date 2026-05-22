namespace ARCP.Core

open System.Text.Json

/// Logging severity tag carried in a `log` event body.
[<RequireQualifiedAccess>]
type LogLevel =
    | Debug
    | Info
    | Warn
    | Error

/// Chunk-encoding flag for streamed result data (spec §8.4).
[<RequireQualifiedAccess>]
type ChunkEncoding =
    | Utf8
    | Base64

/// Job lifecycle status (spec §7.3). Four terminal states.
[<RequireQualifiedAccess>]
type JobStatus =
    | Pending
    | Running
    | Success
    | Error
    | Cancelled
    | TimedOut

[<RequireQualifiedAccess>]
module JobStatus =
    /// Wire string for a `JobStatus`.
    let toWire (s: JobStatus) : string =
        match s with
        | JobStatus.Pending -> "pending"
        | JobStatus.Running -> "running"
        | JobStatus.Success -> "success"
        | JobStatus.Error -> "error"
        | JobStatus.Cancelled -> "cancelled"
        | JobStatus.TimedOut -> "timed_out"

    /// Parse a wire string. Returns `Error` for unknown values; the
    /// codec rejects malformed envelopes upstream.
    let tryOfWire (s: string) : Result<JobStatus, string> =
        match s with
        | "pending" -> Ok JobStatus.Pending
        | "running" -> Ok JobStatus.Running
        | "success" -> Ok JobStatus.Success
        | "error" -> Ok JobStatus.Error
        | "cancelled" -> Ok JobStatus.Cancelled
        | "timed_out" -> Ok JobStatus.TimedOut
        | other -> Error (sprintf "Unknown job status: %s" other)

    /// Throwing form. Preserved for codec dispatch.
    let ofWire (s: string) : JobStatus =
        match tryOfWire s with
        | Ok v -> v
        | Error e -> invalidArg "s" e

/// Outcome inside a `tool_result` event (§8.2).
[<RequireQualifiedAccess>]
type ToolOutcome =
    | Result of value: JsonElement
    | Error of code: string * message: string * retryable: bool

/// `delegate` event body (§10).
type DelegateBody = {
    ChildJobId: string
    Agent: string
    Lease: LeaseGrant
    LeaseConstraints: LeaseConstraints option
}

/// `job.event` body. One DU case per reserved `kind` value (§8.2)
/// plus an `XVendor` arm to round-trip unknown `kind` values for
/// IANA-extension namespaces (§15).
[<RequireQualifiedAccess>]
type JobEventBody =
    | Log of level: LogLevel * message: string
    | Thought of text: string
    | ToolCall of tool: string * args: JsonElement * callId: string
    | ToolResult of callId: string * outcome: ToolOutcome
    | Status of phase: string * message: string option
    | Metric of
        name: string *
        value: decimal *
        unit: string option *
        dimensions: Map<string, string> option
    | ArtifactRef of
        uri: string *
        contentType: string *
        byteSize: int64 option *
        sha256: string option
    | Delegate of body: DelegateBody
    | Progress of
        current: decimal *
        total: decimal option *
        units: string option *
        message: string option
    | ResultChunk of
        resultId: string *
        chunkSeq: int64 *
        data: string *
        encoding: ChunkEncoding *
        more: bool
    | XVendor of kind: string * body: JsonElement

[<RequireQualifiedAccess>]
module JobEventBody =
    /// Wire-level `kind` string for a body.
    let kind (b: JobEventBody) : string =
        match b with
        | JobEventBody.Log _ -> "log"
        | JobEventBody.Thought _ -> "thought"
        | JobEventBody.ToolCall _ -> "tool_call"
        | JobEventBody.ToolResult _ -> "tool_result"
        | JobEventBody.Status _ -> "status"
        | JobEventBody.Metric _ -> "metric"
        | JobEventBody.ArtifactRef _ -> "artifact_ref"
        | JobEventBody.Delegate _ -> "delegate"
        | JobEventBody.Progress _ -> "progress"
        | JobEventBody.ResultChunk _ -> "result_chunk"
        | JobEventBody.XVendor(k, _) -> k

[<RequireQualifiedAccess>]
module StatusPhases =
    [<Literal>]
    let CredentialRotated = "credential_rotated"
