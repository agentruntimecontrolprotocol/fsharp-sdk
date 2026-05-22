namespace ARCP.Core

open System
open Cysharp

/// Read-only echo of the lease restrictions baked into a provisioned
/// credential by the upstream service (spec §9.8.1).
type CredentialConstraints =
    {
        CostBudget: string list option
        ModelUse: string list option
        ExpiresAt: DateTimeOffset option
    }

/// One credential as it appears on the wire in
/// `job.accepted.payload.credentials` (spec §9.8.1).
///
/// `Value` is a secret. Do not write it to logs, traces, or
/// subscriber-only fan-out paths.
type Credential =
    {
        Id: string
        Scheme: string
        Value: string
        Endpoint: string
        Profile: string option
        Constraints: CredentialConstraints option
    }

[<RequireQualifiedAccess>]
module CredentialId =
    /// Mint a fresh credential id with the spec-readable `cred_`
    /// prefix for provisioners that lack their own id space.
    let newId () : string = "cred_" + Ulid.NewUlid().ToString()
