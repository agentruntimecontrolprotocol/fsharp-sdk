namespace ARCP.Messages

open System.Text.Json
open ARCP
open ARCP.Errors
open ARCP.Envelope
open ARCP.Extensions
open ARCP.Messages.Session
open ARCP.Messages.Control
open ARCP.Messages.Execution
open ARCP.Messages.Streaming
open ARCP.Messages.Human
open ARCP.Messages.Permissions
open ARCP.Messages.Subscriptions
open ARCP.Messages.Artifacts
open ARCP.Messages.Telemetry

/// <summary>
/// Unified message type discriminated union covering every core message
/// (RFC §6.2) plus an <c>Extension</c> escape hatch. The wire format does
/// NOT use the FSharp.SystemTextJson tag for this DU — instead the envelope
/// carries a raw <c>type</c> string and an opaque payload, and
/// <see cref="ofWireType"/> / <see cref="wireType"/> handle the mapping.
/// </summary>
module Registry =

    /// <summary>Opaque carrier for a namespaced extension (RFC §21).</summary>
    type ExtensionEnvelope = { Type: string; Payload: JsonElement }

    /// <summary>Every concrete ARCP message variant.</summary>
    type MessageType =
        // Session (§9)
        | SessionOpen of SessionOpen
        | SessionChallenge of SessionChallenge
        | SessionAuthenticate of SessionAuthenticate
        | SessionAccepted of SessionAccepted
        | SessionUnauthenticated of SessionUnauthenticated
        | SessionRejected of SessionRejected
        | SessionRefresh of SessionRefresh
        | SessionEvicted of SessionEvicted
        | SessionClose of SessionClose
        // Control (§7, §10.4, §10.5, §10.7, §12)
        | Ping of Ping
        | Pong of Pong
        | Ack of Ack
        | Nack of Nack
        | Cancel of Cancel
        | CancelAccepted of CancelAccepted
        | CancelRefused of CancelRefused
        | Interrupt of Interrupt
        | Resume of Resume
        | Backpressure of Backpressure
        | CheckpointCreate of CheckpointCreate
        | CheckpointRestore of CheckpointRestore
        // Execution (§10)
        | ToolInvoke of ToolInvoke
        | ToolResult of ToolResult
        | ToolError of ToolError
        | JobAccepted of JobAccepted
        | JobStarted of JobStarted
        | JobProgress of JobProgress
        | JobHeartbeat of JobHeartbeat
        | JobCheckpoint of JobCheckpoint
        | JobCompleted of JobCompleted
        | JobFailed of JobFailed
        | JobCancelled of JobCancelled
        | JobSchedule of JobSchedule
        | WorkflowStart of WorkflowStart
        | WorkflowComplete of WorkflowComplete
        | AgentDelegate of AgentDelegate
        | AgentHandoff of AgentHandoff
        // Streaming (§11)
        | StreamOpen of StreamOpen
        | StreamChunk of StreamChunk
        | StreamClose of StreamClose
        | StreamError of StreamError
        // Human (§14)
        | HumanInputRequest of HumanInputRequest
        | HumanInputResponse of HumanInputResponse
        | HumanChoiceRequest of HumanChoiceRequest
        | HumanChoiceResponse of HumanChoiceResponse
        | HumanInputCancelled of HumanInputCancelled
        // Permissions (§15)
        | PermissionRequest of PermissionRequest
        | PermissionGrant of PermissionGrant
        | PermissionDenied of PermissionDenied
        | LeaseGranted of LeaseGranted
        | LeaseExtended of LeaseExtended
        | LeaseRevoked of LeaseRevoked
        | LeaseRefresh of LeaseRefresh
        // Subscriptions (§13)
        | Subscribe of Subscribe
        | SubscribeAccepted of SubscribeAccepted
        | SubscribeEvent of SubscribeEvent
        | Unsubscribe of Unsubscribe
        | SubscribeClosed of SubscribeClosed
        // Artifacts (§16)
        | ArtifactPut of ArtifactPut
        | ArtifactFetch of ArtifactFetch
        | ArtifactRef of ArtifactRef
        | ArtifactRelease of ArtifactRelease
        // Telemetry (§17)
        | EventEmit of EventEmit
        | LogEntry of LogEntry
        | MetricSample of MetricSample
        | TraceSpan of TraceSpan
        // Extensions (§21)
        | Extension of ExtensionEnvelope

    /// <summary>The canonical wire <c>type</c> string for a message.</summary>
    let wireType (msg: MessageType) : string =
        match msg with
        | SessionOpen _ -> "session.open"
        | SessionChallenge _ -> "session.challenge"
        | SessionAuthenticate _ -> "session.authenticate"
        | SessionAccepted _ -> "session.accepted"
        | SessionUnauthenticated _ -> "session.unauthenticated"
        | SessionRejected _ -> "session.rejected"
        | SessionRefresh _ -> "session.refresh"
        | SessionEvicted _ -> "session.evicted"
        | SessionClose _ -> "session.close"
        | Ping _ -> "ping"
        | Pong _ -> "pong"
        | Ack _ -> "ack"
        | Nack _ -> "nack"
        | Cancel _ -> "cancel"
        | CancelAccepted _ -> "cancel.accepted"
        | CancelRefused _ -> "cancel.refused"
        | Interrupt _ -> "interrupt"
        | Resume _ -> "resume"
        | Backpressure _ -> "backpressure"
        | CheckpointCreate _ -> "checkpoint.create"
        | CheckpointRestore _ -> "checkpoint.restore"
        | ToolInvoke _ -> "tool.invoke"
        | ToolResult _ -> "tool.result"
        | ToolError _ -> "tool.error"
        | JobAccepted _ -> "job.accepted"
        | JobStarted _ -> "job.started"
        | JobProgress _ -> "job.progress"
        | JobHeartbeat _ -> "job.heartbeat"
        | JobCheckpoint _ -> "job.checkpoint"
        | JobCompleted _ -> "job.completed"
        | JobFailed _ -> "job.failed"
        | JobCancelled _ -> "job.cancelled"
        | JobSchedule _ -> "job.schedule"
        | WorkflowStart _ -> "workflow.start"
        | WorkflowComplete _ -> "workflow.complete"
        | AgentDelegate _ -> "agent.delegate"
        | AgentHandoff _ -> "agent.handoff"
        | StreamOpen _ -> "stream.open"
        | StreamChunk _ -> "stream.chunk"
        | StreamClose _ -> "stream.close"
        | StreamError _ -> "stream.error"
        | HumanInputRequest _ -> "human.input.request"
        | HumanInputResponse _ -> "human.input.response"
        | HumanChoiceRequest _ -> "human.choice.request"
        | HumanChoiceResponse _ -> "human.choice.response"
        | HumanInputCancelled _ -> "human.input.cancelled"
        | PermissionRequest _ -> "permission.request"
        | PermissionGrant _ -> "permission.grant"
        | PermissionDenied _ -> "permission.deny"
        | LeaseGranted _ -> "lease.granted"
        | LeaseExtended _ -> "lease.extended"
        | LeaseRevoked _ -> "lease.revoked"
        | LeaseRefresh _ -> "lease.refresh"
        | Subscribe _ -> "subscribe"
        | SubscribeAccepted _ -> "subscribe.accepted"
        | SubscribeEvent _ -> "subscribe.event"
        | Unsubscribe _ -> "unsubscribe"
        | SubscribeClosed _ -> "subscribe.closed"
        | ArtifactPut _ -> "artifact.put"
        | ArtifactFetch _ -> "artifact.fetch"
        | ArtifactRef _ -> "artifact.ref"
        | ArtifactRelease _ -> "artifact.release"
        | EventEmit _ -> "event.emit"
        | LogEntry _ -> "log"
        | MetricSample _ -> "metric"
        | TraceSpan _ -> "trace.span"
        | Extension e -> e.Type

    /// <summary>Extract the payload as a <see cref="JsonElement"/>.</summary>
    let toPayloadElement (msg: MessageType) : JsonElement =
        match msg with
        | SessionOpen p -> Json.toElement p
        | SessionChallenge p -> Json.toElement p
        | SessionAuthenticate p -> Json.toElement p
        | SessionAccepted p -> Json.toElement p
        | SessionUnauthenticated p -> Json.toElement p
        | SessionRejected p -> Json.toElement p
        | SessionRefresh p -> Json.toElement p
        | SessionEvicted p -> Json.toElement p
        | SessionClose p -> Json.toElement p
        | Ping p -> Json.toElement p
        | Pong p -> Json.toElement p
        | Ack p -> Json.toElement p
        | Nack p -> Json.toElement p
        | Cancel p -> Json.toElement p
        | CancelAccepted p -> Json.toElement p
        | CancelRefused p -> Json.toElement p
        | Interrupt p -> Json.toElement p
        | Resume p -> Json.toElement p
        | Backpressure p -> Json.toElement p
        | CheckpointCreate p -> Json.toElement p
        | CheckpointRestore p -> Json.toElement p
        | ToolInvoke p -> Json.toElement p
        | ToolResult p -> Json.toElement p
        | ToolError p -> Json.toElement p
        | JobAccepted p -> Json.toElement p
        | JobStarted p -> Json.toElement p
        | JobProgress p -> Json.toElement p
        | JobHeartbeat p -> Json.toElement p
        | JobCheckpoint p -> Json.toElement p
        | JobCompleted p -> Json.toElement p
        | JobFailed p -> Json.toElement p
        | JobCancelled p -> Json.toElement p
        | JobSchedule p -> Json.toElement p
        | WorkflowStart p -> Json.toElement p
        | WorkflowComplete p -> Json.toElement p
        | AgentDelegate p -> Json.toElement p
        | AgentHandoff p -> Json.toElement p
        | StreamOpen p -> Json.toElement p
        | StreamChunk p -> Json.toElement p
        | StreamClose p -> Json.toElement p
        | StreamError p -> Json.toElement p
        | HumanInputRequest p -> Json.toElement p
        | HumanInputResponse p -> Json.toElement p
        | HumanChoiceRequest p -> Json.toElement p
        | HumanChoiceResponse p -> Json.toElement p
        | HumanInputCancelled p -> Json.toElement p
        | PermissionRequest p -> Json.toElement p
        | PermissionGrant p -> Json.toElement p
        | PermissionDenied p -> Json.toElement p
        | LeaseGranted p -> Json.toElement p
        | LeaseExtended p -> Json.toElement p
        | LeaseRevoked p -> Json.toElement p
        | LeaseRefresh p -> Json.toElement p
        | Subscribe p -> Json.toElement p
        | SubscribeAccepted p -> Json.toElement p
        | SubscribeEvent p -> Json.toElement p
        | Unsubscribe p -> Json.toElement p
        | SubscribeClosed p -> Json.toElement p
        | ArtifactPut p -> Json.toElement p
        | ArtifactFetch p -> Json.toElement p
        | ArtifactRef p -> Json.toElement p
        | ArtifactRelease p -> Json.toElement p
        | EventEmit p -> Json.toElement p
        | LogEntry p -> Json.toElement p
        | MetricSample p -> Json.toElement p
        | TraceSpan p -> Json.toElement p
        | Extension e -> e.Payload

    let private tryFrom<'T> (element: JsonElement) (ctor: 'T -> MessageType) : Result<MessageType, ARCPError> =
        try
            Ok(ctor (Json.fromElement<'T> element))
        with ex ->
            Error(InvalidArgument("payload", ex.Message))

    /// <summary>
    /// Decode a wire <c>type</c> + payload to a typed
    /// <see cref="MessageType"/>. Unknown types are routed through
    /// <see cref="ARCP.Extensions.decide"/>: namespaced extensions become an
    /// <see cref="Extension"/>; core-prefixed unknowns are
    /// <see cref="Unimplemented"/>; the rest are
    /// <see cref="InvalidArgument"/>.
    /// </summary>
    let ofWireType (envType: string) (payload: JsonElement) : Result<MessageType, ARCPError> =
        match envType with
        | "session.open" -> tryFrom<SessionOpen> payload SessionOpen
        | "session.challenge" -> tryFrom<SessionChallenge> payload SessionChallenge
        | "session.authenticate" -> tryFrom<SessionAuthenticate> payload SessionAuthenticate
        | "session.accepted" -> tryFrom<SessionAccepted> payload SessionAccepted
        | "session.unauthenticated" -> tryFrom<SessionUnauthenticated> payload SessionUnauthenticated
        | "session.rejected" -> tryFrom<SessionRejected> payload SessionRejected
        | "session.refresh" -> tryFrom<SessionRefresh> payload SessionRefresh
        | "session.evicted" -> tryFrom<SessionEvicted> payload SessionEvicted
        | "session.close" -> tryFrom<SessionClose> payload SessionClose
        | "ping" -> tryFrom<Ping> payload Ping
        | "pong" -> tryFrom<Pong> payload Pong
        | "ack" -> tryFrom<Ack> payload Ack
        | "nack" -> tryFrom<Nack> payload Nack
        | "cancel" -> tryFrom<Cancel> payload Cancel
        | "cancel.accepted" -> tryFrom<CancelAccepted> payload CancelAccepted
        | "cancel.refused" -> tryFrom<CancelRefused> payload CancelRefused
        | "interrupt" -> tryFrom<Interrupt> payload Interrupt
        | "resume" -> tryFrom<Resume> payload Resume
        | "backpressure" -> tryFrom<Backpressure> payload Backpressure
        | "checkpoint.create" -> tryFrom<CheckpointCreate> payload CheckpointCreate
        | "checkpoint.restore" -> tryFrom<CheckpointRestore> payload CheckpointRestore
        | "tool.invoke" -> tryFrom<ToolInvoke> payload ToolInvoke
        | "tool.result" -> tryFrom<ToolResult> payload ToolResult
        | "tool.error" -> tryFrom<ToolError> payload ToolError
        | "job.accepted" -> tryFrom<JobAccepted> payload JobAccepted
        | "job.started" -> tryFrom<JobStarted> payload JobStarted
        | "job.progress" -> tryFrom<JobProgress> payload JobProgress
        | "job.heartbeat" -> tryFrom<JobHeartbeat> payload JobHeartbeat
        | "job.checkpoint" -> tryFrom<JobCheckpoint> payload JobCheckpoint
        | "job.completed" -> tryFrom<JobCompleted> payload JobCompleted
        | "job.failed" -> tryFrom<JobFailed> payload JobFailed
        | "job.cancelled" -> tryFrom<JobCancelled> payload JobCancelled
        | "job.schedule" -> tryFrom<JobSchedule> payload JobSchedule
        | "workflow.start" -> tryFrom<WorkflowStart> payload WorkflowStart
        | "workflow.complete" -> tryFrom<WorkflowComplete> payload WorkflowComplete
        | "agent.delegate" -> tryFrom<AgentDelegate> payload AgentDelegate
        | "agent.handoff" -> tryFrom<AgentHandoff> payload AgentHandoff
        | "stream.open" -> tryFrom<StreamOpen> payload StreamOpen
        | "stream.chunk" -> tryFrom<StreamChunk> payload StreamChunk
        | "stream.close" -> tryFrom<StreamClose> payload StreamClose
        | "stream.error" -> tryFrom<StreamError> payload StreamError
        | "human.input.request" -> tryFrom<HumanInputRequest> payload HumanInputRequest
        | "human.input.response" -> tryFrom<HumanInputResponse> payload HumanInputResponse
        | "human.choice.request" -> tryFrom<HumanChoiceRequest> payload HumanChoiceRequest
        | "human.choice.response" -> tryFrom<HumanChoiceResponse> payload HumanChoiceResponse
        | "human.input.cancelled" -> tryFrom<HumanInputCancelled> payload HumanInputCancelled
        | "permission.request" -> tryFrom<PermissionRequest> payload PermissionRequest
        | "permission.grant" -> tryFrom<PermissionGrant> payload PermissionGrant
        | "permission.deny" -> tryFrom<PermissionDenied> payload PermissionDenied
        | "lease.granted" -> tryFrom<LeaseGranted> payload LeaseGranted
        | "lease.extended" -> tryFrom<LeaseExtended> payload LeaseExtended
        | "lease.revoked" -> tryFrom<LeaseRevoked> payload LeaseRevoked
        | "lease.refresh" -> tryFrom<LeaseRefresh> payload LeaseRefresh
        | "subscribe" -> tryFrom<Subscribe> payload Subscribe
        | "subscribe.accepted" -> tryFrom<SubscribeAccepted> payload SubscribeAccepted
        | "subscribe.event" -> tryFrom<SubscribeEvent> payload SubscribeEvent
        | "unsubscribe" -> tryFrom<Unsubscribe> payload Unsubscribe
        | "subscribe.closed" -> tryFrom<SubscribeClosed> payload SubscribeClosed
        | "artifact.put" -> tryFrom<ArtifactPut> payload ArtifactPut
        | "artifact.fetch" -> tryFrom<ArtifactFetch> payload ArtifactFetch
        | "artifact.ref" -> tryFrom<ArtifactRef> payload ArtifactRef
        | "artifact.release" -> tryFrom<ArtifactRelease> payload ArtifactRelease
        | "event.emit" -> tryFrom<EventEmit> payload EventEmit
        | "log" -> tryFrom<LogEntry> payload LogEntry
        | "metric" -> tryFrom<MetricSample> payload MetricSample
        | "trace.span" -> tryFrom<TraceSpan> payload TraceSpan
        | other ->
            if isExtensionType other then
                Ok(Extension { Type = other; Payload = payload })
            else if isCoreType other then
                Error(Unimplemented(sprintf "core type %s not implemented" other))
            else
                Error(InvalidArgument("type", sprintf "%s is neither core nor a recognized extension" other))

    /// <summary>
    /// Convenience constructors that wrap a payload in an
    /// <see cref="Envelope`1"/> with the right wire <c>type</c>.
    /// </summary>
    module Envelopes =
        let private make (msg: MessageType) : Envelope<MessageType> = Envelope.create (wireType msg) msg

        let sessionOpen (p: SessionOpen) = make (SessionOpen p)
        let sessionChallenge (p: SessionChallenge) = make (SessionChallenge p)
        let sessionAuthenticate (p: SessionAuthenticate) = make (SessionAuthenticate p)
        let sessionAccepted (p: SessionAccepted) = make (SessionAccepted p)
        let sessionUnauthenticated (p: SessionUnauthenticated) = make (SessionUnauthenticated p)
        let sessionRejected (p: SessionRejected) = make (SessionRejected p)
        let sessionClose (p: SessionClose) = make (SessionClose p)
        let ping (p: Ping) = make (Ping p)
        let pong (p: Pong) = make (Pong p)
        let ack (p: Ack) = make (Ack p)
        let nack (p: Nack) = make (Nack p)
