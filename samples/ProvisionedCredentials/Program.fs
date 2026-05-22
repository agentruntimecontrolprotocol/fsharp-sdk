module ArcpSamples.ProvisionedCredentials

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Runtime
open ArcpSamples.SampleHarness

type StaticProvisioner(revoked: HashSet<string>) =
    interface ICredentialProvisioner with
        member _.IssueAsync(ctx, _ct) =
            let credential: Credential =
                {
                    Id = CredentialId.newId ()
                    Scheme = "bearer"
                    Value = "sk_sample_secret"
                    Endpoint = "https://llm.example.test"
                    Profile = Some "sample"
                    Constraints =
                        Some
                            {
                                CostBudget = Map.tryFind Capabilities.CostBudget ctx.Lease.Capabilities
                                ModelUse = Map.tryFind Capabilities.ModelUse ctx.Lease.Capabilities
                                ExpiresAt = ctx.LeaseConstraints |> Option.map (fun c -> c.ExpiresAt)
                            }
                }

            Task.FromResult [ credential ]

        member _.RevokeAsync(credentialId, _ct) =
            lock revoked (fun () -> revoked.Add credentialId |> ignore)
            Task.FromResult true

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let revoked = HashSet<string>()
            let provisioner = StaticProvisioner(revoked) :> ICredentialProvisioner
            let store = InMemoryCredentialStore() :> ICredentialStore

            let features =
                Set.ofList [ Features.ProvisionedCredentials; Features.ModelUse; Features.LeaseExpiresAt ]

            let withCredentials (options: ArcpServerOptions) =
                { options with
                    Provisioner = Some provisioner
                    CredentialStore = Some store
                }

            let! p =
                connectWithOptions
                    withCredentials
                    (fun s ->
                        s.RegisterAgent(
                            "model-user",
                            fun ctx ->
                                task {
                                    do!
                                        ctx.ValidateOpAsync(
                                            Capabilities.ModelUse,
                                            "tier-fast/gpt-4o-mini",
                                            ctx.CancellationToken
                                        )

                                    return jsonString "model call allowed"
                                }
                        ))
                    features

            let lease =
                Lease.empty
                |> Lease.withCapability Capabilities.ModelUse [ "tier-fast/*" ]
                |> Lease.withCapability Capabilities.CostBudget [ "USD:1.00" ]

            let! handle =
                p.Client.SubmitAsync(
                    {
                        Agent = "model-user"
                        Input = jsonInt 0
                        LeaseRequest = Some lease
                        LeaseConstraints =
                            Some
                                {
                                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes 5.0
                                }
                        IdempotencyKey = None
                        MaxRuntimeSec = None
                    },
                    CancellationToken.None
                )

            match handle.Credentials with
            | [ credential ] -> writeLine (sprintf "issued %s for %s" credential.Id credential.Endpoint)
            | other -> writeErr (sprintf "expected one credential, got %d" other.Length)

            let! result = handle.Result

            match result with
            | Ok r -> writeLine (sprintf "finished with %s" (JobStatus.toWire r.FinalStatus))
            | Error e -> writeErr (sprintf "failed: %s" (ARCPError.code e))

            do! Task.Delay 50
            writeLine (sprintf "revoked credentials: %d" revoked.Count)
            do! teardown p
            return 0
        })
