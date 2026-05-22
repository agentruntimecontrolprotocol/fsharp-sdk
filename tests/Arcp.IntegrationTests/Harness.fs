module ARCP.IntegrationTests.Harness

open System
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport
open ARCP.Runtime
open ARCP.Runtime.Auth

type ConnectedPair = {
    Client: ArcpClient
    Server: ArcpServer
    Cancel: CancellationTokenSource
}

let connectWithOptions
        (configureOptions: ArcpServerOptions -> ArcpServerOptions)
        (configure: ArcpServer -> unit)
        (features: Set<string>)
        : Task<ConnectedPair> =
    task {
        let cts = new CancellationTokenSource()
        let serverOptions =
            { ArcpServerOptions.defaults with
                Features = features
                BearerVerifier = DevModeBearerVerifier() }
            |> configureOptions
        let server =
            ArcpServer(serverOptions)
        configure server
        let clientT, serverT = MemoryTransport.CreatePair()
        let serverTask = server.HandleSessionAsync(serverT, cts.Token)
        let client =
            new ArcpClient(
                clientT,
                { ArcpClientOptions.defaults with
                    Auth = AuthScheme.Bearer "test-token"
                    Features = features })
        let! _ = client.ConnectAsync CancellationToken.None
        return { Client = client; Server = server; Cancel = cts }
    }

let connect (configure: ArcpServer -> unit) (features: Set<string>) : Task<ConnectedPair> =
    connectWithOptions id configure features

let teardown (p: ConnectedPair) : Task =
    task {
        try do! p.Client.CloseAsync(None, CancellationToken.None) with _ -> ()
        try p.Cancel.Cancel() with _ -> ()
        try p.Cancel.Dispose() with _ -> ()
    } :> Task

let mkRequest (agent: string) : JobSubmitRequest =
    {
        Agent = agent
        Input = Json.serializeToElement<int> 0
        LeaseRequest = None
        LeaseConstraints = None
        IdempotencyKey = None
        MaxRuntimeSec = None
    }
