module ArcpSamples.SampleHarness

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport
open ARCP.Runtime
open ARCP.Runtime.Auth

/// Shared scaffold for the samples. Each sample creates a paired
/// `ArcpClient` + `ArcpServer` over an in-process memory transport,
/// registers one or more agent handlers, and runs the demonstration.
///
/// Samples are intentionally simple: the goal is to read each one
/// as a single-screen example of one ARCP feature.

let writeLine (s: string) = Console.Out.WriteLine s
let writeErr (s: string) = Console.Error.WriteLine s

let runAsync (work: unit -> Task<int>) : int =
    work().GetAwaiter().GetResult()

type Pair = {
    Client: ArcpClient
    Server: ArcpServer
    ServerTask: Task
    Cancel: CancellationTokenSource
}

let private makeServer (configure: ArcpServer -> unit) (features: Set<string>) : ArcpServer =
    let server =
        ArcpServer(
            { ArcpServerOptions.defaults with
                Features = features
                BearerVerifier = DevModeBearerVerifier() :> IBearerVerifier })
    configure server
    server

/// Connect a client to a freshly-built runtime over an in-memory
/// transport pair. The returned `Pair` owns lifetimes; dispose it
/// at the end of `main`.
let connect (configureServer: ArcpServer -> unit) (features: Set<string>) : Task<Pair> =
    task {
        let cts = new CancellationTokenSource()
        let server = makeServer configureServer features
        let clientTransport, serverTransport = MemoryTransport.CreatePair()
        let serverTask = server.HandleSessionAsync(serverTransport, cts.Token)
        let clientOpts =
            { ArcpClientOptions.defaults with
                Auth = AuthScheme.Bearer "demo-token"
                Features = features }
        let client = new ArcpClient(clientTransport, clientOpts)
        let! _ = client.ConnectAsync CancellationToken.None
        return {
            Client = client
            Server = server
            ServerTask = serverTask
            Cancel = cts
        }
    }

let teardown (p: Pair) : Task =
    task {
        try
            do! p.Client.CloseAsync(None, CancellationToken.None)
        with _ -> ()
        try p.Cancel.Cancel() with _ -> ()
        try p.Cancel.Dispose() with _ -> ()
    } :> Task

/// Convenience: serialise a string to a `JsonElement` for input/output.
let jsonString (s: string) : JsonElement =
    Json.serializeToElement<string> s

let jsonInt (n: int) : JsonElement =
    Json.serializeToElement<int> n

let echoAgent : ArcpAgentHandler =
    fun _ctx -> task { return jsonString "echoed" }
