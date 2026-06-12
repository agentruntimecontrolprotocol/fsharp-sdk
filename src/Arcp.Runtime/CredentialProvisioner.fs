namespace ARCP.Runtime

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Runtime.Auth

/// Inputs handed to `ICredentialProvisioner.IssueAsync` at job
/// acceptance (spec §9.8.2).
type CredentialIssueContext =
    {
        JobId: JobId
        Principal: IPrincipal
        Lease: LeaseGrant
        LeaseConstraints: LeaseConstraints option
        ParentJobId: JobId option
    }

/// Outcome of a single upstream revocation attempt (§9.8.2). Splits
/// the old boolean so success and permanent failure are distinct: a
/// credential that permanently failed to revoke must NOT be treated as
/// revoked (it stays outstanding for operator reconciliation).
[<RequireQualifiedAccess>]
type RevocationOutcome =
    /// Confirmed revoked upstream.
    | Revoked
    /// Transient failure; the registry should retry.
    | Transient
    /// Permanent failure; further retries are futile.
    | Permanent

/// Vendor-neutral provisioner. Implementations for LiteLLM, Anthropic
/// admin keys, or internal gateways live outside core runtime wiring.
type ICredentialProvisioner =
    /// Mint credentials for `ctx`. The returned list may be empty;
    /// callers must treat each `Value` as a secret.
    abstract member IssueAsync: ctx: CredentialIssueContext * ct: CancellationToken -> Task<Credential list>

    /// Revoke a credential upstream. Return `Transient` for failures
    /// that should be retried, `Revoked` on confirmed success, and
    /// `Permanent` when further retries are futile.
    abstract member RevokeAsync: credentialId: string * ct: CancellationToken -> Task<RevocationOutcome>

/// Durable per-credential store. Deployments that need revocation to
/// survive process restart should back this with their own database.
type ICredentialStore =
    abstract member RecordIssuedAsync: jobId: JobId * cred: Credential -> Task
    abstract member RecordRevokedAsync: jobId: JobId * credentialId: string -> Task
    abstract member ListOutstandingAsync: unit -> Task<(JobId * string) list>

/// In-process credential store for tests, samples, and development.
type InMemoryCredentialStore() =
    let outstanding = ConcurrentDictionary<string, JobId * string>()

    interface ICredentialStore with
        member _.RecordIssuedAsync(jobId, cred) =
            outstanding.[cred.Id] <- (jobId, cred.Id)
            Task.CompletedTask

        member _.RecordRevokedAsync(_jobId, credentialId) =
            outstanding.TryRemove credentialId |> ignore
            Task.CompletedTask

        member _.ListOutstandingAsync() =
            outstanding |> Seq.map (fun kv -> kv.Value) |> Seq.toList |> Task.FromResult

/// Default provisioner used when no credential support is configured.
type NoOpCredentialProvisioner() =
    interface ICredentialProvisioner with
        member _.IssueAsync(_ctx, _ct) = Task.FromResult []

        member _.RevokeAsync(_id, _ct) =
            Task.FromResult RevocationOutcome.Revoked
