namespace ARCP.Core

open System
open System.Text.Json

/// Wire-canonical message vocabulary (spec §6, §7, §8).
///
/// Eighteen DU cases mirror the eighteen `type` strings the
/// protocol uses. Each payload record carries the spec's exact
/// field names; snake_case wire encoding is handled by
/// `Json.Options` (`SnakeCaseLower`).
/// Payload of `session.hello.payload.resume` (spec §6.3).

type ResumeRequest =
    {
        SessionId: string
        ResumeToken: string
        LastEventSeq: int64
    }

/// `session.hello` payload (spec §6.1, §6.2).

type SessionHelloPayload =
    {
        Client: ClientIdentity
        Auth: AuthPayload
        Capabilities: HelloCapabilities
    }

/// `session.resume` payload (spec §6.3). Sent in place of
/// `session.hello` to reattach to an existing session and replay
/// buffered events from `last_event_seq`.

type SessionResumePayload =
    {
        SessionId: string
        ResumeToken: string
        LastEventSeq: int64
    }

/// `session.welcome` payload (spec §6.2).

type SessionWelcomePayload =
    {
        Runtime: RuntimeIdentity
        ResumeToken: string
        ResumeWindowSec: int
        HeartbeatIntervalSec: int option
        Capabilities: WelcomeCapabilities
    }

/// `session.ping` payload (spec §6.4).

type SessionPingPayload =
    {
        Nonce: string
        SentAt: DateTimeOffset
    }

/// `session.pong` payload (spec §6.4).

type SessionPongPayload =
    {
        PingNonce: string
        ReceivedAt: DateTimeOffset
    }

/// `session.ack` payload (spec §6.5).

type SessionAckPayload = { LastProcessedSeq: int64 }

/// Filter shape for `session.list_jobs.payload.filter` (spec §6.6).

type JobListFilter =
    {
        Status: JobStatus list option
        Agent: string option
        CreatedAfter: DateTimeOffset option
    }

/// `session.list_jobs` payload (spec §6.6).

type SessionListJobsPayload =
    {
        Filter: JobListFilter option
        Limit: int option
        Cursor: string option
    }

/// One row of `session.jobs.payload.jobs` (spec §6.6).

type JobSummary =
    {
        JobId: string
        Agent: string
        Status: JobStatus
        Lease: LeaseGrant
        ParentJobId: string option
        CreatedAt: DateTimeOffset
        TraceId: string option
        LastEventSeq: int64
    }

/// `session.jobs` payload (spec §6.6).

type SessionJobsPayload =
    {
        RequestId: string
        Jobs: JobSummary list
        NextCursor: string option
    }

/// `session.close` payload (spec §6.7). Sent by the client to
/// terminate a session gracefully.

type SessionClosePayload = { Reason: string option }

/// `session.closed` payload (spec §6.7). Runtime acknowledgement of
/// a `session.close`.

type SessionClosedPayload = { Reason: string option }

/// `session.error` payload (spec §12).

type SessionErrorPayload =
    {
        Code: string
        Message: string
        Retryable: bool
        Details: JsonElement option
    }

/// `job.submit` payload (spec §7.1).

type JobSubmitPayload =
    {
        Agent: string
        Input: JsonElement
        LeaseRequest: LeaseGrant option
        LeaseConstraints: LeaseConstraints option
        IdempotencyKey: string option
        MaxRuntimeSec: int option
    }

/// `job.accepted` payload (spec §7.1).

type JobAcceptedPayload =
    {
        JobId: string
        Lease: LeaseGrant
        LeaseConstraints: LeaseConstraints option
        Budget: Map<string, decimal> option
        Credentials: Credential list option
        AcceptedAt: DateTimeOffset
        TraceId: string option
    }

/// `job.event` payload (spec §8.1).

type JobEventPayload =
    {
        Kind: string
        Ts: DateTimeOffset
        Body: JobEventBody
    }

/// `job.result` payload (spec §7.3, §8.4).

type JobResultPayload =
    {
        FinalStatus: JobStatus
        Result: JsonElement option
        ResultId: string option
        ResultSize: int64 option
        Summary: string option
    }

/// `job.error` payload (spec §7.3, §12).

type JobErrorPayload =
    {
        FinalStatus: JobStatus
        Code: string
        Message: string
        Retryable: bool
        Details: JsonElement option
    }

/// `job.cancel` payload (spec §7.4).

type JobCancelPayload =
    { JobId: string; Reason: string option }

/// `job.cancelled` payload (spec §7.4). Runtime acknowledgement of a
/// `job.cancel` request; the terminal `job.error(CANCELLED)` follows.

type JobCancelledPayload = { JobId: string }

/// `job.subscribe` payload (spec §7.6).

type JobSubscribePayload =
    {
        JobId: string
        FromEventSeq: int64 option
        History: bool option
    }

/// `job.subscribed` payload (spec §7.6).

type JobSubscribedPayload =
    {
        JobId: string
        CurrentStatus: JobStatus
        Agent: string
        Lease: LeaseGrant
        ParentJobId: string option
        TraceId: string option
        SubscribedFrom: int64
        Replayed: bool
    }

/// `job.unsubscribe` payload (spec §7.6).

type JobUnsubscribePayload = { JobId: string }

[<RequireQualifiedAccess>]
type Message =
    | SessionHello of SessionHelloPayload
    | SessionResume of SessionResumePayload
    | SessionWelcome of SessionWelcomePayload
    | SessionPing of SessionPingPayload
    | SessionPong of SessionPongPayload
    | SessionAck of SessionAckPayload
    | SessionListJobs of SessionListJobsPayload
    | SessionJobs of SessionJobsPayload
    | SessionClose of SessionClosePayload
    | SessionClosed of SessionClosedPayload
    | SessionError of SessionErrorPayload
    | JobSubmit of JobSubmitPayload
    | JobAccepted of JobAcceptedPayload
    | JobEvent of JobEventPayload
    | JobResult of JobResultPayload
    | JobError of JobErrorPayload
    | JobCancel of JobCancelPayload
    | JobCancelled of JobCancelledPayload
    | JobSubscribe of JobSubscribePayload
    | JobSubscribed of JobSubscribedPayload
    | JobUnsubscribe of JobUnsubscribePayload

[<RequireQualifiedAccess>]
module Message =
    /// Wire `type` string for each message case.
    let typeOf (m: Message) : string =
        match m with
        | Message.SessionHello _ -> "session.hello"
        | Message.SessionResume _ -> "session.resume"
        | Message.SessionWelcome _ -> "session.welcome"
        | Message.SessionPing _ -> "session.ping"
        | Message.SessionPong _ -> "session.pong"
        | Message.SessionAck _ -> "session.ack"
        | Message.SessionListJobs _ -> "session.list_jobs"
        | Message.SessionJobs _ -> "session.jobs"
        | Message.SessionClose _ -> "session.close"
        | Message.SessionClosed _ -> "session.closed"
        | Message.SessionError _ -> "session.error"
        | Message.JobSubmit _ -> "job.submit"
        | Message.JobAccepted _ -> "job.accepted"
        | Message.JobEvent _ -> "job.event"
        | Message.JobResult _ -> "job.result"
        | Message.JobError _ -> "job.error"
        | Message.JobCancel _ -> "job.cancel"
        | Message.JobCancelled _ -> "job.cancelled"
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
