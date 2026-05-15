namespace ARCP

/// Curated public surface re-exports for consumers who reference
/// the umbrella `Arcp` package. Each binder pulls the canonical
/// name out of its home project so consumers can `open ARCP.Public`
/// (single import) instead of opening every sub-namespace.
[<RequireQualifiedAccess>]
module Public =
    type ArcpClient = ARCP.Client.ArcpClient
    type ArcpClientOptions = ARCP.Client.ArcpClientOptions
    type JobHandle = ARCP.Client.JobHandle
    type JobSubmitRequest = ARCP.Client.JobSubmitRequest
    type SubscribeOptions = ARCP.Client.SubscribeOptions
    type SessionContext = ARCP.Client.SessionContext
    type ITransport = ARCP.Client.ITransport

    type ArcpServer = ARCP.Runtime.ArcpServer
    type ArcpServerOptions = ARCP.Runtime.ArcpServerOptions
    type ArcpAgentHandler = ARCP.Runtime.ArcpAgentHandler
    type JobContext = ARCP.Runtime.JobContext
    type IPrincipal = ARCP.Runtime.Auth.IPrincipal
    type IBearerVerifier = ARCP.Runtime.Auth.IBearerVerifier
    type StaticBearerVerifier = ARCP.Runtime.Auth.StaticBearerVerifier
    type DevModeBearerVerifier = ARCP.Runtime.Auth.DevModeBearerVerifier

    type ARCPError = ARCP.Core.ARCPError
    type ArcpException = ARCP.Core.ArcpException
    type Envelope = ARCP.Core.Envelope
    type Message = ARCP.Core.Message
    type JobEventBody = ARCP.Core.JobEventBody
    type JobStatus = ARCP.Core.JobStatus
    type LeaseGrant = ARCP.Core.LeaseGrant
    type LeaseConstraints = ARCP.Core.LeaseConstraints
    type SessionId = ARCP.Core.SessionId
    type JobId = ARCP.Core.JobId
    type MessageId = ARCP.Core.MessageId
    type ResultId = ARCP.Core.ResultId
    type ClientIdentity = ARCP.Core.ClientIdentity
    type RuntimeIdentity = ARCP.Core.RuntimeIdentity
    type AuthScheme = ARCP.Core.AuthScheme
    type AgentInventory = ARCP.Core.AgentInventory
    type AgentInventoryEntry = ARCP.Core.AgentInventoryEntry
    type LogLevel = ARCP.Core.LogLevel
    type ToolOutcome = ARCP.Core.ToolOutcome
    type ChunkEncoding = ARCP.Core.ChunkEncoding
    type JobListFilter = ARCP.Core.JobListFilter
    type JobSummary = ARCP.Core.JobSummary
