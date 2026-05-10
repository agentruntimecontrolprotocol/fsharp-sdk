namespace ARCP.Messages

open System
open ARCP.Ids

/// <summary>
/// Session-level payload records (RFC §9). The session handshake is a
/// four-message exchange: <c>session.open</c> -&gt;
/// <c>session.challenge</c> -&gt; <c>session.authenticate</c> -&gt;
/// <c>session.accepted</c>, with <c>session.unauthenticated</c> /
/// <c>session.rejected</c> as failure terminals.
/// </summary>
module Session =

    /// <summary>Credential block (RFC §9.1).</summary>
    type AuthBlock =
        {
            Scheme: string
            Token: string option
            Fingerprint: string option
        }

    /// <summary>Attested client identity (RFC §9.1).</summary>
    type ClientIdentity =
        {
            Kind: string
            Version: string
            Fingerprint: string option
            Principal: string option
        }

    /// <summary>Server runtime identity (RFC §9.2).</summary>
    type RuntimeIdentity =
        {
            Kind: string
            Version: string
            Fingerprint: string option
            TrustLevel: string option
        }

    /// <summary>Artifact retention policy (RFC §16).</summary>
    type ArtifactRetention =
        { DefaultSeconds: int; MaxSeconds: int }

    /// <summary>
    /// Negotiated session capability set (RFC §8). Absent booleans MUST be
    /// treated as <c>false</c>.
    /// </summary>
    type Capabilities =
        {
            Streaming: bool
            DurableJobs: bool
            Checkpoints: bool
            BinaryStreams: bool
            AgentHandoff: bool
            HumanInput: bool
            Artifacts: bool
            Subscriptions: bool
            ScheduledJobs: bool
            Anonymous: bool
            Interrupt: bool
            Extensions: string list
            HeartbeatRecovery: string option
            BinaryEncoding: string list
            HeartbeatIntervalSeconds: int option
            ArtifactRetention: ArtifactRetention option
        }

    [<RequireQualifiedAccess>]
    module Capabilities =
        /// <summary>All booleans <c>false</c>, no extensions.</summary>
        let empty: Capabilities =
            {
                Streaming = false
                DurableJobs = false
                Checkpoints = false
                BinaryStreams = false
                AgentHandoff = false
                HumanInput = false
                Artifacts = false
                Subscriptions = false
                ScheduledJobs = false
                Anonymous = false
                Interrupt = false
                Extensions = []
                HeartbeatRecovery = None
                BinaryEncoding = []
                HeartbeatIntervalSeconds = None
                ArtifactRetention = None
            }

    /// <summary>Permission lease metadata (RFC §15.5).</summary>
    type LeaseInfo = { ExpiresAt: DateTimeOffset }

    /// <summary><c>session.open</c> payload (RFC §9.1).</summary>
    type SessionOpen =
        {
            Arcp: string
            Client: ClientIdentity
            Auth: AuthBlock
            Capabilities: Capabilities
        }

    /// <summary><c>session.challenge</c> payload (RFC §9.1).</summary>
    type SessionChallenge =
        {
            Scheme: string
            Challenge: string
            ExpiresAt: DateTimeOffset option
        }

    /// <summary><c>session.authenticate</c> payload (RFC §9.1).</summary>
    type SessionAuthenticate =
        {
            Scheme: string
            Token: string
            Challenge: string option
        }

    /// <summary><c>session.accepted</c> payload (RFC §9.2).</summary>
    type SessionAccepted =
        {
            SessionId: SessionId
            Runtime: RuntimeIdentity
            Capabilities: Capabilities
            Lease: LeaseInfo option
        }

    /// <summary><c>session.unauthenticated</c> payload (RFC §9.1).</summary>
    type SessionUnauthenticated = { Code: string; Reason: string option }

    /// <summary><c>session.rejected</c> payload (RFC §9.1).</summary>
    type SessionRejected = { Code: string; Reason: string option }

    /// <summary><c>session.refresh</c> payload (RFC §9.3).</summary>
    type SessionRefresh =
        {
            Scheme: string
            Challenge: string
            DeadlineMs: int option
        }

    /// <summary><c>session.evicted</c> payload (RFC §9.4).</summary>
    type SessionEvicted = { Code: string; Reason: string option }

    /// <summary><c>session.close</c> payload (RFC §9.5).</summary>
    type SessionClose = { Reason: string option }
