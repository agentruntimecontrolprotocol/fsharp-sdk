module ARCP.IntegrationTests.ProvisionedCredentialsTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime
open ARCP.IntegrationTests.Harness

type private FakeProvisioner() =
    let revocations = ConcurrentBag<string>()

    member _.Revocations = revocations

    interface ICredentialProvisioner with
        member _.IssueAsync(ctx, _ct) =
            let credential: Credential =
                {
                    Id = "cred_" + ctx.JobId.Value
                    Scheme = "bearer"
                    Value = "sk_" + ctx.JobId.Value
                    Endpoint = "https://llm.example.test"
                    Profile = Some "test"
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
            revocations.Add credentialId
            Task.FromResult true

let private withProvisioner (fake: FakeProvisioner) (options: ArcpServerOptions) =
    { options with
        Provisioner = Some(fake :> ICredentialProvisioner)
        CredentialStore = Some(InMemoryCredentialStore() :> ICredentialStore)
    }

let private connectProvisioned fake configure =
    connectWithOptions (withProvisioner fake) configure Features.All

let private waitForRevocations (fake: FakeProvisioner) expected =
    task {
        let deadline = DateTimeOffset.UtcNow.AddSeconds 3.0

        while fake.Revocations.Count < expected && DateTimeOffset.UtcNow < deadline do
            do! Task.Delay 25

        fake.Revocations.Count |> should be (greaterThanOrEqualTo expected)
    }

[<Fact>]
let ``credentials appear in job accepted when provisioner configured`` () =
    task {
        let fake = FakeProvisioner()

        let! p =
            connectProvisioned fake (fun s ->
                s.RegisterAgent(
                    "ok",
                    fun ctx ->
                        task {
                            do! Task.Delay(50, ctx.CancellationToken)
                            return Json.serializeToElement<string> "ok"
                        }
                ))

        let lease =
            Lease.empty
            |> Lease.withCapability Capabilities.ModelUse [ "tier-fast/*" ]
            |> Lease.withCapability Capabilities.CostBudget [ "USD:1.00" ]

        let! handle =
            p.Client.SubmitAsync(
                { mkRequest "ok" with
                    LeaseRequest = Some lease
                },
                CancellationToken.None
            )

        handle.Credentials.Length |> should equal 1
        handle.Credentials.Head.Value |> should equal ("sk_" + handle.JobId.Value)

        handle.Credentials.Head.Constraints.Value.ModelUse
        |> should equal (Some [ "tier-fast/*" ])

        let! _ = handle.Result
        do! teardown p
    }

[<Fact>]
let ``credentials are absent when no provisioner`` () =
    task {
        let! p =
            connect
                (fun s ->
                    s.RegisterAgent(
                        "ok",
                        fun ctx ->
                            task {
                                do! Task.Delay(50, ctx.CancellationToken)
                                return Json.serializeToElement<string> "ok"
                            }
                    ))
                Features.All

        let! handle = p.Client.SubmitAsync(mkRequest "ok", CancellationToken.None)
        handle.Credentials |> should equal ([]: Credential list)
        let! _ = handle.Result
        do! teardown p
    }

[<Fact>]
let ``provisioner revoke called on success`` () =
    task {
        let fake = FakeProvisioner()

        let! p =
            connectProvisioned fake (fun s ->
                s.RegisterAgent("ok", fun _ -> task { return Json.serializeToElement<string> "ok" }))

        let! handle = p.Client.SubmitAsync(mkRequest "ok", CancellationToken.None)
        let! _ = handle.Result
        do! waitForRevocations fake 1
        do! teardown p
    }

[<Fact>]
let ``provisioner revoke called on cancelled`` () =
    task {
        let fake = FakeProvisioner()

        let! p =
            connectProvisioned fake (fun s ->
                s.RegisterAgent(
                    "forever",
                    fun ctx ->
                        task {
                            do! Task.Delay(Timeout.Infinite, ctx.CancellationToken)
                            return Json.serializeToElement<int> 0
                        }
                ))

        let! handle = p.Client.SubmitAsync(mkRequest "forever", CancellationToken.None)
        let! _ = handle.CancelAsync(Some "test", CancellationToken.None)
        let! _ = handle.Result
        do! waitForRevocations fake 1
        do! teardown p
    }

[<Fact>]
let ``provisioner revoke called on error`` () =
    task {
        let fake = FakeProvisioner()

        let! p =
            connectProvisioned fake (fun s ->
                s.RegisterAgent(
                    "boom",
                    fun _ ->
                        task {
                            failwith "boom"
                            return Json.nullElement ()
                        }
                ))

        let! handle = p.Client.SubmitAsync(mkRequest "boom", CancellationToken.None)
        let! _ = handle.Result
        do! waitForRevocations fake 1
        do! teardown p
    }

[<Fact>]
let ``provisioner revoke called on lease expiry`` () =
    task {
        let fake = FakeProvisioner()

        let! p =
            connectProvisioned fake (fun s ->
                s.RegisterAgent(
                    "forever",
                    fun ctx ->
                        task {
                            do! Task.Delay(Timeout.Infinite, ctx.CancellationToken)
                            return Json.serializeToElement<int> 0
                        }
                ))

        let req =
            { mkRequest "forever" with
                LeaseConstraints =
                    Some
                        {
                            ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds 250.0
                        }
            }

        let! handle = p.Client.SubmitAsync(req, CancellationToken.None)
        let! _ = handle.Result
        do! waitForRevocations fake 1
        do! teardown p
    }

[<Fact>]
let ``credential rotated status event emits with new value`` () =
    task {
        let fake = FakeProvisioner()

        let! p =
            connectProvisioned fake (fun s ->
                s.RegisterAgent(
                    "rotate",
                    fun ctx ->
                        task {
                            do! Task.Delay(100, ctx.CancellationToken)
                            let credentialId = "cred_" + ctx.JobId.Value
                            do! ctx.RotateCredentialAsync(credentialId, "sk_rotated", ctx.CancellationToken)
                            return Json.serializeToElement<string> "ok"
                        }
                ))

        let! handle = p.Client.SubmitAsync(mkRequest "rotate", CancellationToken.None)
        use events = handle.Events.GetAsyncEnumerator(CancellationToken.None)
        let! hasEvent = events.MoveNextAsync().AsTask()
        hasEvent |> should equal true

        match events.Current with
        | JobEventBody.Status(phase, Some message) ->
            phase |> should equal StatusPhases.CredentialRotated
            message.Contains("sk_rotated") |> should equal true
        | other -> failwithf "expected credential_rotated status, got %A" other

        let! _ = handle.Result
        do! waitForRevocations fake 1
        do! teardown p
    }

[<Fact>]
let ``list jobs does not expose credentials`` () =
    task {
        let fake = FakeProvisioner()

        let! p =
            connectProvisioned fake (fun s ->
                s.RegisterAgent(
                    "ok",
                    fun ctx ->
                        task {
                            do! Task.Delay(50, ctx.CancellationToken)
                            return Json.serializeToElement<string> "ok"
                        }
                ))

        let! handle = p.Client.SubmitAsync(mkRequest "ok", CancellationToken.None)
        let! jobs = p.Client.ListJobsAsync(None, None, None, CancellationToken.None)
        let wire = Json.serialize jobs
        wire.Contains("sk_" + handle.JobId.Value) |> should equal false
        let! _ = handle.Result
        do! teardown p
    }
