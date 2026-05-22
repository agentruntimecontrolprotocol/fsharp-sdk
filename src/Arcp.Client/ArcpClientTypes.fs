namespace ARCP.Client

open System
open ARCP.Core
open ARCP.Client.Internal

/// Configuration for an `ArcpClient`.
type ArcpClientOptions =
    {
        /// Client identity advertised in `session.hello.payload.client`.
        Client: ClientIdentity
        /// Authentication scheme. `None` sends `auth.scheme = "none"`.
        Auth: AuthScheme
        /// Feature flags the client supports. Pass `Features.All` for
        /// the full surface; narrow to interoperate with a peer that
        /// does not advertise every flag.
        Features: Set<string>
        /// `TimeProvider` for auto-ack scheduling and client-side timers.
        /// Tests should pass `FakeTimeProvider`.
        TimeProvider: TimeProvider
        /// Auto-ack settings. Effective only when `ack` is negotiated.
        AutoAck: AutoAckOptions
    }

[<RequireQualifiedAccess>]
module ArcpClientOptions =
    /// Reasonable defaults: anonymous auth, every feature flag,
    /// system clock, default auto-ack windows.
    let defaults: ArcpClientOptions =
        {
            Client =
                {
                    Name = "arcp-fsharp"
                    Version = Version.Sdk
                }
            Auth = AuthScheme.None
            Features = Features.All
            TimeProvider = TimeProvider.System
            AutoAck = AutoAckOptions.defaults
        }

/// Negotiated session state shared with the caller after
/// `session.welcome` has arrived (spec §6.2).
type SessionContext =
    {
        SessionId: SessionId
        NegotiatedFeatures: Set<string>
        HeartbeatIntervalSec: int option
        ResumeToken: string
        ResumeWindowSec: int
        AgentInventory: AgentInventory
    }

/// Request shape for `job.submit` (spec §7.1).
type JobSubmitRequest =
    {
        Agent: string
        Input: System.Text.Json.JsonElement
        LeaseRequest: LeaseGrant option
        LeaseConstraints: LeaseConstraints option
        IdempotencyKey: string option
        MaxRuntimeSec: int option
    }

/// Options on `job.subscribe` (spec §7.6).
type SubscribeOptions =
    {
        FromEventSeq: int64 option
        History: bool
    }

[<RequireQualifiedAccess>]
module SubscribeOptions =
    /// Live-only subscription with no history replay.
    let defaults: SubscribeOptions = { FromEventSeq = None; History = false }
