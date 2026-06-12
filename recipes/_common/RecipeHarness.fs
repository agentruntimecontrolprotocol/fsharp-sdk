module ArcpRecipes.RecipeHarness

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport
open ARCP.Runtime
open ARCP.Runtime.Auth

type Pair = {
    Client: ArcpClient
    Server: ArcpServer
    ServerTask: Task
    Cancel: CancellationTokenSource
}

let writeLine (message: string) = Console.Out.WriteLine message
let writeErr (message: string) = Console.Error.WriteLine message

let runAsync (work: unit -> Task<int>) : int =
    work().GetAwaiter().GetResult()

let jsonString (value: string) : JsonElement =
    Json.serializeToElement value

let jsonInt (value: int) : JsonElement =
    Json.serializeToElement value

let jsonObj<'T> (value: 'T) : JsonElement =
    Json.serializeToElement value

let connectWithOptions
        (configureOptions: ArcpServerOptions -> ArcpServerOptions)
        (configureServer: ArcpServer -> unit)
        (features: Set<string>)
        : Task<Pair> =
    task {
        let cts = new CancellationTokenSource()
        let options =
            { ArcpServerOptions.defaults with
                Features = features
                BearerVerifier = DevModeBearerVerifier() :> IBearerVerifier }
            |> configureOptions
        let server = new ArcpServer(options)
        configureServer server
        let clientTransport, serverTransport = MemoryTransport.CreatePair()
        let serverTask = server.HandleSessionAsync(serverTransport, cts.Token)
        let clientOptions =
            { ArcpClientOptions.defaults with
                Auth = AuthScheme.Bearer "demo-token"
                Features = features }
        let client = new ArcpClient(clientTransport, clientOptions)
        let! _ = client.ConnectAsync CancellationToken.None
        return {
            Client = client
            Server = server
            ServerTask = serverTask
            Cancel = cts
        }
    }

let connect (configureServer: ArcpServer -> unit) (features: Set<string>) : Task<Pair> =
    connectWithOptions id configureServer features

let teardown (pair: Pair) : Task =
    task {
        try
            do! pair.Client.CloseAsync(None, CancellationToken.None)
        with _ -> ()
        try pair.Cancel.Cancel() with _ -> ()
        pair.Cancel.Dispose()
    } :> Task
