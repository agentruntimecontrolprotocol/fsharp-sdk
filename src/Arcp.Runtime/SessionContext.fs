namespace ARCP.Runtime

open System
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime.Auth
open ARCP.Runtime.Internal
open ARCP.Runtime.Store

/// Per-session state held by the runtime.
///
/// One instance lives for the lifetime of an accepted session
/// (across the resume window). Fields mutable here are the small
/// pieces of session state — ack pointer, last seen pong — that
/// the protocol expects to evolve. The lease, agent inventory,
/// and job map are immutable references to the runtime-wide values.
type internal ServerSessionContext = {
    SessionId: SessionId
    Principal: IPrincipal
    NegotiatedFeatures: Set<string>
    HeartbeatIntervalSec: int option
    ResumeToken: string
    ResumeWindowSec: int
    Transport: ITransport
    EventLog: EventLog
    mutable LastAckedSeq: int64
    mutable LastInboundAt: DateTimeOffset
}

[<RequireQualifiedAccess>]
module internal ServerSessionContext =
    let create
        (sessionId: SessionId)
        (principal: IPrincipal)
        (features: Set<string>)
        (heartbeat: int option)
        (resumeToken: string)
        (resumeWindow: int)
        (transport: ITransport)
        (log: EventLog)
        (now: DateTimeOffset)
        : ServerSessionContext =
        {
            SessionId = sessionId
            Principal = principal
            NegotiatedFeatures = features
            HeartbeatIntervalSec = heartbeat
            ResumeToken = resumeToken
            ResumeWindowSec = resumeWindow
            Transport = transport
            EventLog = log
            LastAckedSeq = 0L
            LastInboundAt = now
        }
