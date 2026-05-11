module ARCP.IntegrationTests.StdioTransportTests

open System
open System.IO
open System.IO.Pipelines
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Logging.Abstractions
open ARCP
open ARCP.Errors
open ARCP.Messages.Session
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

/// Build a pair of (TextReader, TextWriter) wired via System.IO.Pipelines
/// such that what is written to the writer is readable from the reader.
let private pipePair () : TextReader * TextWriter =
    let pipe = Pipe()
    let readStream = pipe.Reader.AsStream()
    let writeStream = pipe.Writer.AsStream()
    let reader = new System.IO.StreamReader(readStream, Encoding.UTF8)
    let writer = new System.IO.StreamWriter(writeStream, Encoding.UTF8)
    writer.AutoFlush <- true
    reader :> TextReader, writer :> TextWriter

let private makePair () : ITransport * ITransport =
    // A reads what B writes; B reads what A writes.
    let aReader, bWriter = pipePair ()
    let bReader, aWriter = pipePair ()
    let a = Stdio.create (aReader, aWriter)
    let b = Stdio.create (bReader, bWriter)
    a, b

let private startRuntime (transport: ITransport) =
    let tokens = dict [ "secret", "alice" ]
    let validator = BearerValidator tokens :> IAuthValidator

    let opts =
        { RuntimeOptions.defaults with
            OfferedCapabilities = Capabilities.empty
            HeartbeatInterval = TimeSpan.FromSeconds 30.0
        }

    let runtime = new Runtime(transport, validator, NullLogger.Instance, opts)
    let _ = runtime.StartAsync CancellationToken.None
    runtime

[<Fact>]
let ``stdio: valid bearer token yields SessionAccepted`` () =
    task {
        let serverT, clientT = makePair ()
        let runtime = startRuntime serverT
        let client = new Client(clientT, Bearer "secret")

        let! result = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        match result with
        | Ok _ -> ()
        | Error e -> failwithf "expected Ok, got %A" e

        do! runtime.StopAsync()
        do! (runtime :> IAsyncDisposable).DisposeAsync()
        do! (clientT :> IAsyncDisposable).DisposeAsync()
        do! (serverT :> IAsyncDisposable).DisposeAsync()
    }

[<Fact>]
let ``stdio: tool invoke round-trip`` () =
    task {
        let serverT, clientT = makePair ()
        let runtime = startRuntime serverT

        runtime.RegisterTool("echo", fun (_ctx: ToolContext) args -> task { return Ok args })

        let client = new Client(clientT, Bearer "secret")
        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        let arg = System.Text.Json.JsonSerializer.SerializeToElement<int>(42)
        let! result = client.InvokeAsync("echo", arg)

        match result with
        | Ok(Some v) -> Assert.Equal(42, v.GetInt32())
        | other -> failwithf "expected Ok(Some 42), got %A" other

        do! runtime.StopAsync()
        do! (runtime :> IAsyncDisposable).DisposeAsync()
        do! (clientT :> IAsyncDisposable).DisposeAsync()
        do! (serverT :> IAsyncDisposable).DisposeAsync()
    }

[<Fact(Skip = "requires ARCP.Cli serve --stdio (Phase 7)")>]
let ``stdio: subprocess round-trip`` () = task { return () }
