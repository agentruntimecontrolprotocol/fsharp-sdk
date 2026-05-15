namespace ARCP.Core

/// Protocol identification and feature flag constants.
[<RequireQualifiedAccess>]
module Version =
    /// Wire-level protocol version emitted as the envelope `arcp` field.
    [<Literal>]
    let Protocol = "1"

    /// SDK package version. Pinned alongside `Directory.Build.props`.
    [<Literal>]
    let Sdk = "1.0.0"

/// Feature flags exchanged via `session.hello.capabilities.features`
/// and `session.welcome.capabilities.features` (spec §6.2).
///
/// The effective feature set for a session is the intersection of
/// the two sides. Either peer MUST NOT use a feature that is not in
/// the intersection.
[<RequireQualifiedAccess>]
module Features =
    [<Literal>]
    let Heartbeat = "heartbeat"

    [<Literal>]
    let Ack = "ack"

    [<Literal>]
    let ListJobs = "list_jobs"

    [<Literal>]
    let Subscribe = "subscribe"

    [<Literal>]
    let LeaseExpiresAt = "lease_expires_at"

    [<Literal>]
    let CostBudget = "cost.budget"

    [<Literal>]
    let Progress = "progress"

    [<Literal>]
    let ResultChunk = "result_chunk"

    [<Literal>]
    let AgentVersions = "agent_versions"

    /// All feature flags this SDK understands.
    let All : Set<string> =
        Set.ofList
            [ Heartbeat
              Ack
              ListJobs
              Subscribe
              LeaseExpiresAt
              CostBudget
              Progress
              ResultChunk
              AgentVersions ]

    /// Compute the effective feature set from the two halves of the
    /// capability exchange. Pure set intersection.
    let intersect (clientFeatures: Set<string>) (runtimeFeatures: Set<string>) : Set<string> =
        Set.intersect clientFeatures runtimeFeatures
