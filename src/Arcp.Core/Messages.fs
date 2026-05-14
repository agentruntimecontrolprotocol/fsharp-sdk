namespace ARCP.Core

open System
open System.Text.Json

/// Wire-canonical message vocabulary (spec §6, §7, §8).
///
/// Eighteen DU cases mirror the eighteen `type` strings the
/// protocol uses. Each payload record carries the spec's exact
/// field names; snake_case wire encoding is handled by
/// `Json.Options` (`SnakeCaseLower`).

[<RequireQualifiedAccess>]
type LogLevel =
    | Debug
    | Info
    | Warn
    | Error

[<RequireQualifiedAccess>]
type ChunkEncoding =
    | Utf8
    | Base64

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
    let toWire (s: JobStatus) : string =
        match s with
        | JobStatus.Pending -> "pending"
        | JobStatus.Running -> "running"
        | JobStatus.Success -> "success"
        | JobStatus.Error -> "error"
        | JobStatus.Cancelled -> "cancelled"
        | JobStatus.TimedOut -> "timed_out"

    let ofWire (s: string) : JobStatus =
        match s with
        | "pending" -> JobStatus.Pending
        | "running" -> JobStatus.Running
        | "success" -> JobStatus.Success
        | "error" -> JobStatus.Error
        | "cancelled" -> JobStatus.Cancelled
        | "timed_out" -> JobStatus.TimedOut
        | other -> failwithf "Unknown job status: %s" other

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

// ---------------------------------------------------------------------------
// Session payloads
// ---------------------------------------------------------------------------

type SessionHelloPayload = {
    Client: ClientIdentity
    Auth: AuthPayload
    Capabilities: HelloCapabilities
    Resume: ResumeRequest option
}

and ResumeRequest = {
    SessionId: string
    ResumeToken: string
    LastEventSeq: int64
}

type SessionWelcomePayload = {
    Runtime: RuntimeIdentity
    ResumeToken: string
    ResumeWindowSec: int
    HeartbeatIntervalSec: int option
    Capabilities: WelcomeCapabilities
}

type SessionPingPayload = {
    Nonce: string
    SentAt: DateTimeOffset
}

type SessionPongPayload = {
    PingNonce: string
    ReceivedAt: DateTimeOffset
}

type SessionAckPayload = {
    LastProcessedSeq: int64
}

type JobListFilter = {
    Status: JobStatus list option
    Agent: string option
    CreatedAfter: DateTimeOffset option
}

type SessionListJobsPayload = {
    Filter: JobListFilter option
    Limit: int option
    Cursor: string option
}

type JobSummary = {
    JobId: string
    Agent: string
    Status: JobStatus
    Lease: LeaseGrant
    ParentJobId: string option
    CreatedAt: DateTimeOffset
    TraceId: string option
    LastEventSeq: int64
}

type SessionJobsPayload = {
    RequestId: string
    Jobs: JobSummary list
    NextCursor: string option
}

type SessionByePayload = {
    Reason: string option
}

type SessionErrorPayload = {
    Code: string
    Message: string
    Retryable: bool
    Details: JsonElement option
}

// ---------------------------------------------------------------------------
// Job payloads
// ---------------------------------------------------------------------------

type JobSubmitPayload = {
    Agent: string
    Input: JsonElement
    LeaseRequest: LeaseGrant option
    LeaseConstraints: LeaseConstraints option
    IdempotencyKey: string option
    MaxRuntimeSec: int option
}

type JobAcceptedPayload = {
    JobId: string
    Lease: LeaseGrant
    LeaseConstraints: LeaseConstraints option
    Budget: Map<string, decimal> option
    AcceptedAt: DateTimeOffset
    TraceId: string option
}

type JobEventPayload = {
    Kind: string
    Ts: DateTimeOffset
    Body: JobEventBody
}

type JobResultPayload = {
    FinalStatus: JobStatus
    Result: JsonElement option
    ResultId: string option
    ResultSize: int64 option
    Summary: string option
}

type JobErrorPayload = {
    FinalStatus: JobStatus
    Code: string
    Message: string
    Retryable: bool
    Details: JsonElement option
}

type JobCancelPayload = {
    JobId: string
    Reason: string option
}

type JobSubscribePayload = {
    JobId: string
    FromEventSeq: int64 option
    History: bool option
}

type JobSubscribedPayload = {
    JobId: string
    CurrentStatus: JobStatus
    Agent: string
    Lease: LeaseGrant
    ParentJobId: string option
    TraceId: string option
    SubscribedFrom: int64
    Replayed: bool
}

type JobUnsubscribePayload = {
    JobId: string
}

// ---------------------------------------------------------------------------
// Message DU
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Message =
    | SessionHello of SessionHelloPayload
    | SessionWelcome of SessionWelcomePayload
    | SessionPing of SessionPingPayload
    | SessionPong of SessionPongPayload
    | SessionAck of SessionAckPayload
    | SessionListJobs of SessionListJobsPayload
    | SessionJobs of SessionJobsPayload
    | SessionBye of SessionByePayload
    | SessionError of SessionErrorPayload
    | JobSubmit of JobSubmitPayload
    | JobAccepted of JobAcceptedPayload
    | JobEvent of JobEventPayload
    | JobResult of JobResultPayload
    | JobError of JobErrorPayload
    | JobCancel of JobCancelPayload
    | JobSubscribe of JobSubscribePayload
    | JobSubscribed of JobSubscribedPayload
    | JobUnsubscribe of JobUnsubscribePayload

[<RequireQualifiedAccess>]
module Message =
    /// Wire `type` string for each message case.
    let typeOf (m: Message) : string =
        match m with
        | Message.SessionHello _ -> "session.hello"
        | Message.SessionWelcome _ -> "session.welcome"
        | Message.SessionPing _ -> "session.ping"
        | Message.SessionPong _ -> "session.pong"
        | Message.SessionAck _ -> "session.ack"
        | Message.SessionListJobs _ -> "session.list_jobs"
        | Message.SessionJobs _ -> "session.jobs"
        | Message.SessionBye _ -> "session.bye"
        | Message.SessionError _ -> "session.error"
        | Message.JobSubmit _ -> "job.submit"
        | Message.JobAccepted _ -> "job.accepted"
        | Message.JobEvent _ -> "job.event"
        | Message.JobResult _ -> "job.result"
        | Message.JobError _ -> "job.error"
        | Message.JobCancel _ -> "job.cancel"
        | Message.JobSubscribe _ -> "job.subscribe"
        | Message.JobSubscribed _ -> "job.subscribed"
        | Message.JobUnsubscribe _ -> "job.unsubscribe"

    /// Heartbeats (`session.ping`/`session.pong`) and `session.ack`
    /// MUST NOT be counted in the session's `event_seq` (§6.4, §6.5).
    let countsInEventSeq (m: Message) : bool =
        match m with
        | Message.SessionPing _
        | Message.SessionPong _
        | Message.SessionAck _ -> false
        | _ -> true
