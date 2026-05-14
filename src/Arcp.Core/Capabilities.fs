namespace ARCP.Core

/// Capability surfaces exchanged via `session.hello` / `session.welcome`.
/// See spec §6.2.

type ClientIdentity = {
    Name: string
    Version: string
}

type RuntimeIdentity = {
    Name: string
    Version: string
}

[<RequireQualifiedAccess>]
type AuthScheme =
    | Bearer of token: string
    | None

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

/// Capabilities advertised on the wire by the client.
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

/// An entry in the rich agent inventory (spec §6.2 / §7.5).
type AgentInventoryEntry = {
    Name: string
    Versions: string list
    Default: string option
}

/// Agent inventory advertised in `session.welcome.payload.capabilities.agents`.
/// The runtime always emits the rich shape when `agent_versions` is
/// in the negotiated feature set; otherwise the flat shape is used.
[<RequireQualifiedAccess>]
type AgentInventory =
    | Flat of names: string list
    | Rich of entries: AgentInventoryEntry list

/// Capabilities advertised on the wire by the runtime.
type WelcomeCapabilities = {
    Encodings: string list
    Features: Set<string>
    Agents: AgentInventory
}
