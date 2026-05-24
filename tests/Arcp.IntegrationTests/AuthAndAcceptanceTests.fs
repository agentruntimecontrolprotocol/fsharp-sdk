module ARCP.IntegrationTests.AuthAndAcceptanceTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport
open ARCP.Runtime
open ARCP.Runtime.Auth
open ARCP.IntegrationTests.Harness

let private connectClient (server: ArcpServer) (auth: AuthScheme) : Task<ArcpClient * CancellationTokenSource> =
    task {
        let cts = new CancellationTokenSource()
        let ct, st = MemoryTransport.CreatePair()
        let serverTask = server.HandleSessionAsync(st, cts.Token)

        let client =
            new ArcpClient(
                ct,
                { ArcpClientOptions.defaults with
                    Auth = auth
                    Features = Features.All
                }
            )

        return client, cts
    }

[<Fact>]
let ``anonymous auth is rejected when AllowAnonymousAuth = false (default)`` () =
    task {
        let server =
            ArcpServer(
                { ArcpServerOptions.defaults with
                    BearerVerifier = StaticBearerVerifier(readOnlyDict [ "secret", "alice" ])
                }
            )

        server.RegisterAgent("ok", fun _ -> task { return Json.serializeToElement<int> 0 })
        let! (client, cts) = connectClient server AuthScheme.None

        let mutable failed = false

        try
            let! _ = client.ConnectAsync CancellationToken.None
            ()
        with _ ->
            failed <- true

        failed |> should equal true

        try
            cts.Cancel()
        with _ ->
            ()
    }

[<Fact>]
let ``bearer auth with a valid token succeeds and authenticates`` () =
    task {
        let server =
            ArcpServer(
                { ArcpServerOptions.defaults with
                    BearerVerifier = StaticBearerVerifier(readOnlyDict [ "secret", "alice" ])
                }
            )

        server.RegisterAgent("ok", fun _ -> task { return Json.serializeToElement<int> 0 })
        let! (client, cts) = connectClient server (AuthScheme.Bearer "secret")
        let! ctx = client.ConnectAsync CancellationToken.None
        ctx.SessionId.Value |> should not' (be NullOrEmptyString)

        try
            do! client.CloseAsync(None, CancellationToken.None)
        with _ ->
            ()

        try
            cts.Cancel()
        with _ ->
            ()
    }

[<Fact>]
let ``anonymous auth succeeds when AllowAnonymousAuth = true`` () =
    task {
        let server =
            ArcpServer(
                { ArcpServerOptions.defaults with
                    AllowAnonymousAuth = true
                }
            )

        server.RegisterAgent("ok", fun _ -> task { return Json.serializeToElement<int> 0 })
        let! (client, cts) = connectClient server AuthScheme.None
        let! ctx = client.ConnectAsync CancellationToken.None
        ctx.SessionId.Value |> should not' (be NullOrEmptyString)

        try
            do! client.CloseAsync(None, CancellationToken.None)
        with _ ->
            ()

        try
            cts.Cancel()
        with _ ->
            ()
    }

/// Provisioner that always fails — used to exercise the unwind path in
/// JobSubmitFlow when credential issuance fails after registration.
type private FailingProvisioner() =
    interface ICredentialProvisioner with
        member _.IssueAsync(_, _) =
            task { return raise (ArcpException(ARCPError.InternalError "boom")) }

        member _.RevokeAsync(_, _) = Task.FromResult true

[<Fact>]
let ``provisioner failure unwinds the job — list_jobs returns nothing`` () =
    task {
        let! p =
            connectWithOptions
                (fun o ->
                    { o with
                        Provisioner = Some(FailingProvisioner() :> ICredentialProvisioner)
                        CredentialStore = Some(InMemoryCredentialStore() :> ICredentialStore)
                    })
                (fun s -> s.RegisterAgent("ok", fun _ -> task { return Json.serializeToElement<int> 0 }))
                Features.All

        let req =
            { mkRequest "ok" with
                LeaseRequest = Some Lease.empty
            }

        let mutable submitError: ARCPError option = None

        try
            let! _ = p.Client.SubmitAsync(req, CancellationToken.None)
            ()
        with :? ArcpException as ex ->
            submitError <- Some ex.Error

        // Either submit raised, or its handle resolves to an error. Both
        // are acceptable; what matters is that no job is listed.
        do! Task.Delay 50
        let! summaries = p.Client.ListJobsAsync(None, None, None, CancellationToken.None)
        summaries.Jobs |> List.length |> should equal 0
        do! teardown p
    }
