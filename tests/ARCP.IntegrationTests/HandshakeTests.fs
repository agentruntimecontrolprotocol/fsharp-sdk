module ARCP.IntegrationTests.HandshakeTests

open System.Collections.Generic
open System.Threading
open Xunit
open Microsoft.Extensions.Logging.Abstractions
open ARCP.Errors
open ARCP.Messages.Session
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

let private startRuntime (transport: ITransport) (validator: IAuthValidator) (offered: Capabilities) =
    let opts =
        { RuntimeOptions.defaults with
            OfferedCapabilities = offered
        }

    let runtime = new Runtime(transport, validator, NullLogger.Instance, opts)
    let task = runtime.StartAsync CancellationToken.None
    runtime, task

[<Fact>]
let ``valid bearer token yields SessionAccepted`` () =
    task {
        let serverT, clientT = Memory.createPair ()

        let tokens = dict [ "secret", "alice" ]
        let validator = BearerValidator tokens :> IAuthValidator
        let runtime, _ = startRuntime serverT validator Capabilities.empty

        let client = new Client(clientT, Bearer "secret")
        let! result = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        match result with
        | Ok _ -> ()
        | Error e -> failwithf "expected Ok, got %A" e

        do! runtime.StopAsync()
        do! (runtime :> System.IAsyncDisposable).DisposeAsync()
    }

[<Fact>]
let ``wrong bearer token yields SessionRejected with UNAUTHENTICATED`` () =
    task {
        let serverT, clientT = Memory.createPair ()
        let tokens = dict [ "secret", "alice" ]
        let validator = BearerValidator tokens :> IAuthValidator
        let runtime, _ = startRuntime serverT validator Capabilities.empty

        let client = new Client(clientT, Bearer "wrong")
        let! result = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        match result with
        | Error(Unauthenticated msg) -> Assert.Contains("UNAUTHENTICATED", msg)
        | other -> failwithf "expected Unauthenticated, got %A" other

        do! runtime.StopAsync()
        do! (runtime :> System.IAsyncDisposable).DisposeAsync()
    }

[<Fact>]
let ``required-but-unsupported capability yields UNIMPLEMENTED`` () =
    task {
        let serverT, clientT = Memory.createPair ()
        let tokens = dict [ "secret", "alice" ]
        let validator = BearerValidator tokens :> IAuthValidator
        // server offers no agent_handoff
        let runtime, _ = startRuntime serverT validator Capabilities.empty

        let client = new Client(clientT, Bearer "secret")

        let requested =
            { Capabilities.empty with
                AgentHandoff = true
            }

        let! result = client.OpenAsync(requested, CancellationToken.None)

        match result with
        | Error(Unauthenticated msg) -> Assert.Contains("UNIMPLEMENTED", msg)
        | other -> failwithf "expected UNIMPLEMENTED rejection, got %A" other

        do! runtime.StopAsync()
        do! (runtime :> System.IAsyncDisposable).DisposeAsync()
    }
