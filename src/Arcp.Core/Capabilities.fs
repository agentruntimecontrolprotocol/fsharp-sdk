namespace ARCP.Core

/// Capability surfaces exchanged via `session.hello` / `session.welcome`.
/// See spec §6.2.

/// Client identity advertised in `session.hello.payload.client`.
type ClientIdentity = {
    Name: string
    Version: string
}

/// Runtime identity advertised in `session.welcome.payload.runtime`.
type RuntimeIdentity = {
    Name: string
    Version: string
}

/// Authentication scheme carried in `session.hello.payload.auth`
/// (spec §6.1). v1.0 ships bearer-only.
[<RequireQualifiedAccess>]
type AuthScheme =
    /// Plain bearer token.
    | Bearer of token: string
    /// No authentication. Used for stdio child processes inside a
    /// trust boundary.
    | None

/// Wire shape of the `auth` envelope payload, separated from
/// `AuthScheme` because the wire form preserves the raw scheme
/// string for forward-compatibility.
type AuthPayload = {
    Scheme: string
    Token: string option
}

[<RequireQualifiedAccess>]
module AuthPayload =
    let ofScheme (scheme: AuthScheme) : AuthPayload =
        match scheme with
        | AuthScheme.Bearer t -> { Scheme = "bearer"; Token = Some t }
        | AuthScheme.None -> { Scheme = "none"; Token = None }

/// Capabilities advertised by the client in
/// `session.hello.payload.capabilities` (spec §6.2).
type HelloCapabilities = {
    Encodings: string list
    Features: Set<string>
}

[<RequireQualifiedAccess>]
module HelloCapabilities =
    /// Default: JSON encoding only; advertise the full set of SDK
    /// feature flags. Consumers can narrow.
    let defaults : HelloCapabilities = {
        Encodings = [ "json" ]
        Features = Features.All
    }

/// One entry of the rich agent inventory (spec §6.2, §7.5).
type AgentInventoryEntry = {
    Name: string
    Versions: string list
    Default: string option
}

/// Agent inventory advertised in `session.welcome.payload.capabilities.agents`.
/// The runtime always emits the rich shape when `agent_versions` is
/// in the negotiated feature set; otherwise the flat shape is used.
/// Agent inventory advertised in
/// `session.welcome.payload.capabilities.agents`. The flat shape is
/// emitted when `agent_versions` is not in the negotiated feature
/// set; the rich shape includes per-agent version info.
[<RequireQualifiedAccess>]
type AgentInventory =
    | Flat of names: string list
    | Rich of entries: AgentInventoryEntry list

/// Capabilities advertised by the runtime in
/// `session.welcome.payload.capabilities` (spec §6.2).
type WelcomeCapabilities = {
    Encodings: string list
    Features: Set<string>
    Agents: AgentInventory
}
