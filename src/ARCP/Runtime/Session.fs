namespace ARCP.Runtime

open ARCP.Ids
open ARCP.Messages.Session

/// <summary>
/// Session-handshake state machine (RFC §9). The runtime drives a session
/// through these states based on the messages it sees from the peer.
/// </summary>
module Session =

    /// <summary>States a server-side session can be in.</summary>
    type SessionState =
        /// <summary>Initial state, awaiting <c>session.open</c>.</summary>
        | Unauthenticated
        /// <summary>Sent <c>session.challenge</c>; awaiting <c>session.authenticate</c>.</summary>
        | AwaitingAuthenticate of challenge: string
        /// <summary>Issued <c>session.accepted</c>; protocol is in steady state.</summary>
        | Authenticated of
            principal: string *
            sessionId: SessionId *
            negotiatedCapabilities: Capabilities *
            lease: LeaseInfo option
        /// <summary>Terminal: connection torn down.</summary>
        | Closed of reason: string

    /// <summary>
    /// Negotiate capabilities (RFC §8). Currently a simple intersection of
    /// the client request with the server's offered set; any required-but-
    /// unsupported capability returns an error string for the caller to
    /// surface as <c>session.rejected</c> with <c>UNIMPLEMENTED</c>.
    /// </summary>
    let negotiate (requested: Capabilities) (offered: Capabilities) : Result<Capabilities, string> =
        let unsupported =
            [
                if requested.Streaming && not offered.Streaming then
                    "streaming"
                if requested.DurableJobs && not offered.DurableJobs then
                    "durable_jobs"
                if requested.Checkpoints && not offered.Checkpoints then
                    "checkpoints"
                if requested.BinaryStreams && not offered.BinaryStreams then
                    "binary_streams"
                if requested.AgentHandoff && not offered.AgentHandoff then
                    "agent_handoff"
                if requested.HumanInput && not offered.HumanInput then
                    "human_input"
                if requested.Artifacts && not offered.Artifacts then
                    "artifacts"
                if requested.Subscriptions && not offered.Subscriptions then
                    "subscriptions"
                if requested.ScheduledJobs && not offered.ScheduledJobs then
                    "scheduled_jobs"
                if requested.Interrupt && not offered.Interrupt then
                    "interrupt"
            ]

        match unsupported with
        | [] ->
            Ok
                { requested with
                    HeartbeatIntervalSeconds = offered.HeartbeatIntervalSeconds
                    ArtifactRetention = offered.ArtifactRetention
                }
        | _ -> Error(System.String.Join(",", unsupported))
