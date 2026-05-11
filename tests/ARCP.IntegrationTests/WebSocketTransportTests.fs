module ARCP.IntegrationTests.WebSocketTransportTests

open System
open System.Text.Json
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

let private startServer (onAccept: ITransport -> Runtime) =
    task {
        let acceptedTcs = TaskCompletionSource<Runtime>()
        let cts = new CancellationTokenSource()

        let options: WebSocket.WebSocketServerOptions =
            {
                Url = "http://127.0.0.1:0"
                OnConnection =
                    fun transport ->
                        task {
                            let runtime = onAccept transport
                            acceptedTcs.TrySetResult(runtime) |> ignore

                            try
                                do! Task.Delay(Timeout.Infinite, cts.Token)
                            with :? OperationCanceledException ->
                                ()
                        }
            }

        let! disposer, uri = WebSocket.runServerAsync options CancellationToken.None

        let combinedDisposer =
            { new IAsyncDisposable with
                member _.DisposeAsync() =
                    ValueTask(
                        task {
                            cts.Cancel()
                            do! disposer.DisposeAsync()
                            cts.Dispose()
                        }
                    )
            }

        return combinedDisposer, uri, acceptedTcs.Task
    }

let private buildRuntime (transport: ITransport) =
    let tokens = dict [ "test-token", "alice" ]
    let validator = BearerValidator tokens :> IAuthValidator

    let opts =
        { RuntimeOptions.defaults with
            OfferedCapabilities = Capabilities.empty
            HeartbeatInterval = TimeSpan.FromSeconds 30.0
        }

    let runtime = new Runtime(transport, validator, NullLogger.Instance, opts)
    runtime.RegisterTool("echo", fun (_ctx: ToolContext) args -> task { return Ok args })
    let _ = runtime.StartAsync CancellationToken.None
    runtime

[<Fact>]
let ``websocket: handshake succeeds`` () =
    task {
        let! disposer, uri, _ = startServer buildRuntime
        let wsUri = WebSocket.toWebSocketUri uri "/ws"

        let! clientTransport = WebSocket.ClientWebSocketTransport.ConnectAsync(wsUri)
        let client = new Client(clientTransport, Bearer "test-token")

        let! result = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        match result with
        | Ok _ -> ()
        | Error e -> failwithf "expected Ok, got %A" e

        do! (clientTransport :> IAsyncDisposable).DisposeAsync()
        do! disposer.DisposeAsync()
    }

[<Fact>]
let ``websocket: tool invoke round-trip`` () =
    task {
        let! disposer, uri, _ = startServer buildRuntime
        let wsUri = WebSocket.toWebSocketUri uri "/ws"

        let! clientTransport = WebSocket.ClientWebSocketTransport.ConnectAsync(wsUri)
        let client = new Client(clientTransport, Bearer "test-token")
        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        let arg = JsonSerializer.SerializeToElement<int>(42)
        let! result = client.InvokeAsync("echo", arg)

        match result with
        | Ok(Some v) -> Assert.Equal(42, v.GetInt32())
        | other -> failwithf "expected Ok(Some 42), got %A" other

        do! (clientTransport :> IAsyncDisposable).DisposeAsync()
        do! disposer.DisposeAsync()
    }

[<Fact>]
let ``websocket: server shutdown closes client cleanly`` () =
    task {
        let! disposer, uri, acceptedTask = startServer buildRuntime
        let wsUri = WebSocket.toWebSocketUri uri "/ws"

        let! clientTransport = WebSocket.ClientWebSocketTransport.ConnectAsync(wsUri)
        let client = new Client(clientTransport, Bearer "test-token")
        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        let! runtime = acceptedTask
        do! runtime.StopAsync()
        do! disposer.DisposeAsync()

        // Subsequent receive on client transport should return None or throw cleanly.
        let receiveTask =
            task {
                try
                    let! env = (clientTransport :> ITransport).ReceiveAsync(CancellationToken.None)
                    return Ok env
                with ex ->
                    return Error ex
            }

        let! _ = receiveTask
        do! (clientTransport :> IAsyncDisposable).DisposeAsync()
    }
