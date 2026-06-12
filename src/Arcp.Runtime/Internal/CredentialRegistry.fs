namespace ARCP.Runtime.Internal

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Runtime

/// Tracks outstanding provisioned credentials per job and orchestrates
/// best-effort revocation with bounded retry.
type internal CredentialRegistry(provisioner: ICredentialProvisioner, store: ICredentialStore) =
    let perJob = ConcurrentDictionary<string, ConcurrentDictionary<string, unit>>()
    let retryDelays = [| 200; 1000; 5000 |]

    // §9.8.2/§14: credentials whose revocation permanently failed, kept
    // for operator inspection (`RevocationFailures`).
    let revocationFailures = ConcurrentDictionary<string, string>()

    let remember (jobId: JobId) (credentialId: string) =
        let ids =
            perJob.GetOrAdd(jobId.Value, fun _ -> ConcurrentDictionary<string, unit>())

        ids.[credentialId] <- ()

    let forget (jobId: JobId) (credentialId: string) =
        match perJob.TryGetValue jobId.Value with
        | true, ids ->
            ids.TryRemove credentialId |> ignore

            if ids.IsEmpty then
                perJob.TryRemove jobId.Value |> ignore
        | _ -> ()

    let revokeWithRetryAsync (jobIdOpt: JobId option) (credentialId: string) (ct: CancellationToken) =
        task {
            let mutable revoked = false
            let mutable stop = false
            let mutable attempt = 0

            // §49: only `Revoked` counts as success. `Permanent` stops
            // retrying but leaves the credential outstanding; exhausted
            // `Transient` retries are also a (non-confirmed) failure.
            while not stop && attempt < retryDelays.Length do
                let! outcome = provisioner.RevokeAsync(credentialId, ct)

                match outcome with
                | RevocationOutcome.Revoked ->
                    revoked <- true
                    stop <- true
                | RevocationOutcome.Permanent -> stop <- true
                | RevocationOutcome.Transient ->
                    do! Task.Delay(retryDelays.[attempt], ct)
                    attempt <- attempt + 1

            if revoked then
                revocationFailures.TryRemove credentialId |> ignore

                match jobIdOpt with
                | Some jobId ->
                    do! store.RecordRevokedAsync(jobId, credentialId)
                    forget jobId credentialId
                | None ->
                    let! outstanding = store.ListOutstandingAsync()

                    for jobId, id in outstanding do
                        if id = credentialId then
                            do! store.RecordRevokedAsync(jobId, credentialId)
                            forget jobId credentialId
            else
                // §9.8.2: permanent revocation failures MUST be logged; §14:
                // surfaced to operators. Record for `RevocationFailures`.
                let jobLabel =
                    jobIdOpt |> Option.map (fun j -> j.Value) |> Option.defaultValue "<unknown>"

                revocationFailures.[credentialId] <- jobLabel

                eprintfn
                    "[ARCP] WARN credential revocation failed permanently: credential_id=%s job_id=%s"
                    credentialId
                    jobLabel
        }

    /// Credentials whose revocation permanently failed, as
    /// `(credentialId, jobId)` pairs. Operators can poll this to find
    /// dangling credentials (§9.8.2, §14).
    member _.RevocationFailures: (string * string) list =
        revocationFailures |> Seq.map (fun kv -> kv.Key, kv.Value) |> List.ofSeq

    member _.Track(jobId: JobId, cred: Credential) : Task =
        task {
            remember jobId cred.Id
            do! store.RecordIssuedAsync(jobId, cred)
        }
        :> Task

    member _.RevokeJobAsync(jobId: JobId, ct: CancellationToken) : Task =
        task {
            let! outstanding = store.ListOutstandingAsync()

            let ids =
                outstanding
                |> List.filter (fun (jid, _) -> jid = jobId)
                |> List.map snd
                |> Set.ofList

            for credentialId in ids do
                do! revokeWithRetryAsync (Some jobId) credentialId ct
        }
        :> Task

    member _.RevokeCredentialAsync(credentialId: string, ct: CancellationToken) : Task =
        revokeWithRetryAsync None credentialId ct :> Task

    /// Resume after restart by replaying all outstanding ids through
    /// the configured provisioner.
    member _.RecoverAsync(ct: CancellationToken) : Task =
        task {
            let! outstanding = store.ListOutstandingAsync()

            for jobId, credentialId in outstanding do
                remember jobId credentialId
                do! revokeWithRetryAsync (Some jobId) credentialId ct
        }
        :> Task
