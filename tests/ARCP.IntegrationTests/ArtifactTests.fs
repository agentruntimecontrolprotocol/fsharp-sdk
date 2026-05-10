module ARCP.IntegrationTests.ArtifactTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Time.Testing
open Microsoft.Extensions.Logging.Abstractions
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Messages.Session
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

let private startPair () =
    let serverT, clientT = Memory.createPair ()
    let tokens = dict [ "secret", "alice" ]
    let validator = BearerValidator tokens :> IAuthValidator

    let opts =
        { RuntimeOptions.defaults with
            OfferedCapabilities =
                { Capabilities.empty with
                    Artifacts = true
                }
        }

    let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
    let _ = runtime.StartAsync CancellationToken.None
    let client = new Client(clientT, Bearer "secret")
    runtime, client

[<Fact>]
let ``put then fetch round trip preserves bytes`` () =
    task {
        let runtime, client = startPair ()

        let! _ =
            client.OpenAsync(
                { Capabilities.empty with
                    Artifacts = true
                },
                CancellationToken.None
            )

        let data = Encoding.UTF8.GetBytes "hello world"
        let! ref = client.PutArtifactAsync("text/plain", data)

        match ref with
        | Ok r ->
            let! got = client.FetchArtifactAsync r.ArtifactId

            match got with
            | Ok bytes -> Assert.Equal<byte[]>(data, bytes)
            | Error e -> failwithf "fetch failed: %A" e
        | Error e -> failwithf "put failed: %A" e

        do! runtime.StopAsync()
    }

[<Fact>]
let ``put with mismatched sha256 fails INVALID_ARGUMENT`` () =
    task {
        use store = new ArtifactStore(TimeProvider.System, TimeSpan.FromHours 1.0)

        let sid = SessionId.create ()
        let data = Encoding.UTF8.GetBytes "abc"
        let b64 = Convert.ToBase64String data

        let! r =
            store.PutAsync(
                sid,
                "text/plain",
                b64,
                sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
            )

        match r with
        | Error(InvalidArgument _) -> ()
        | other -> failwithf "expected InvalidArgument, got %A" other
    }

[<Fact>]
let ``release then fetch returns NOT_FOUND`` () =
    task {
        use store = new ArtifactStore(TimeProvider.System, TimeSpan.FromHours 1.0)

        let sid = SessionId.create ()
        let data = Encoding.UTF8.GetBytes "abc"
        let b64 = Convert.ToBase64String data
        let! r = store.PutAsync(sid, "text/plain", b64)

        match r with
        | Ok rf ->
            let! _ = store.ReleaseAsync rf.ArtifactId
            let! got = store.FetchAsync rf.ArtifactId

            match got with
            | Error(NotFound _) -> ()
            | other -> failwithf "expected NotFound, got %A" other
        | Error e -> failwithf "put failed: %A" e
    }

[<Fact>]
let ``sweeper expires artifacts using FakeTimeProvider`` () =
    task {
        let fake = FakeTimeProvider(DateTimeOffset.UtcNow)

        use store =
            new ArtifactStore(
                fake :> TimeProvider,
                TimeSpan.FromSeconds 5.0,
                TimeSpan.FromHours 24.0,
                TimeSpan.FromSeconds 60.0
            )

        let sid = SessionId.create ()
        let data = Encoding.UTF8.GetBytes "abc"
        let b64 = Convert.ToBase64String data
        let! r = store.PutAsync(sid, "text/plain", b64)

        match r with
        | Ok rf ->
            // Advance past expiry and run sweep.
            fake.Advance(TimeSpan.FromSeconds 10.0)
            do! store.SweepNowAsync()
            let! got = store.FetchAsync rf.ArtifactId

            match got with
            | Error(NotFound _) -> ()
            | other -> failwithf "expected NotFound after expiry, got %A" other
        | Error e -> failwithf "put failed: %A" e
    }
