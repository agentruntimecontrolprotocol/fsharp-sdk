namespace ARCP.Runtime

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Runtime.Auth

/// Inputs handed to `ICredentialProvisioner.IssueAsync` at job
/// acceptance (spec §9.8.2).
type CredentialIssueContext = {
    JobId: JobId
    Principal: IPrincipal
    Lease: LeaseGrant
    LeaseConstraints: LeaseConstraints option
    ParentJobId: JobId option
}

/// Vendor-neutral provisioner. Implementations for LiteLLM, Anthropic
/// admin keys, or internal gateways live outside core runtime wiring.
type ICredentialProvisioner =
    /// Mint credentials for `ctx`. The returned list may be empty;
    /// callers must treat each `Value` as a secret.
    abstract member IssueAsync :
        ctx: CredentialIssueContext * ct: CancellationToken -> Task<Credential list>

    /// Revoke a credential upstream. Return `false` for transient
    /// failures that should be retried, `true` once no further retry
    /// is useful.
    abstract member RevokeAsync :
        credentialId: string * ct: CancellationToken -> Task<bool>

/// Durable per-credential store. Deployments that need revocation to
/// survive process restart should back this with their own database.
type ICredentialStore =
    abstract member RecordIssuedAsync : jobId: JobId * cred: Credential -> Task
    abstract member RecordRevokedAsync : jobId: JobId * credentialId: string -> Task
    abstract member ListOutstandingAsync : unit -> Task<(JobId * string) list>

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
            outstanding
            |> Seq.map (fun kv -> kv.Value)
            |> Seq.toList
            |> Task.FromResult

/// Default provisioner used when no credential support is configured.
type NoOpCredentialProvisioner() =
    interface ICredentialProvisioner with
        member _.IssueAsync(_ctx, _ct) = Task.FromResult []
        member _.RevokeAsync(_id, _ct) = Task.FromResult true
