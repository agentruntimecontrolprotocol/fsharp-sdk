namespace ARCP.Transport

open System
open System.Buffers
open System.Collections.Generic
open System.IO
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ARCP.Envelope
open ARCP.Messages.Registry

/// <summary>
/// WebSocket transport (RFC §22). One text frame == one complete envelope;
/// no binary frames in v0.1.
/// </summary>
module WebSocket =

    let private receiveBufferSize = 8 * 1024

    let private receiveOne
        (socket: System.Net.WebSockets.WebSocket)
        (ct: CancellationToken)
        : Task<Envelope<MessageType> option> =
        task {
            if
                socket.State = WebSocketState.Closed
                || socket.State = WebSocketState.Aborted
                || socket.State = WebSocketState.CloseReceived
            then
                return None
            else
                let buffer = ArrayPool<byte>.Shared.Rent receiveBufferSize
                use ms = new MemoryStream()
                let mutable closed = false
                let mutable finished = false

                try
                    while not finished do
                        let! resultOpt =
                            task {
                                try
                                    let! r = socket.ReceiveAsync(ArraySegment buffer, ct)
                                    return Some r
                                with :? WebSocketException ->
                                    return None
                            }

                        match resultOpt with
                        | None ->
                            closed <- true
                            finished <- true
                        | Some result ->
                            if result.MessageType = WebSocketMessageType.Close then
                                closed <- true
                                finished <- true
                            else
                                ms.Write(buffer, 0, result.Count)

                                if result.EndOfMessage then
                                    finished <- true

                    if closed && ms.Length = 0L then
                        return None
                    else
                        let json = Encoding.UTF8.GetString(ms.ToArray())

                        match Transport.parseEnvelope json with
                        | Ok env -> return Some env
                        | Error err -> return failwithf "WebSocket: failed to parse envelope: %A" err
                finally
                    ArrayPool<byte>.Shared.Return buffer
        }

    let private sendOne
        (socket: System.Net.WebSockets.WebSocket)
        (lock_: SemaphoreSlim)
        (envelope: Envelope<MessageType>)
        (ct: CancellationToken)
        : Task =
        task {
            let json = Transport.serializeEnvelope envelope
            let bytes = Encoding.UTF8.GetBytes json
            do! lock_.WaitAsync(ct)

            try
                do! socket.SendAsync(ArraySegment bytes, WebSocketMessageType.Text, true, ct)
            finally
                lock_.Release() |> ignore
        }

    /// <summary>
    /// Server-side <see cref="ITransport"/> wrapping an accepted
    /// <see cref="System.Net.WebSockets.WebSocket"/> (RFC §22).
    /// </summary>
    type ServerWebSocketTransport(socket: System.Net.WebSockets.WebSocket) =
        let sendLock = new SemaphoreSlim(1, 1)
        let mutable disposed = false

        interface ITransport with
            member _.SendAsync(envelope, ct) = sendOne socket sendLock envelope ct
            member _.ReceiveAsync(ct) = receiveOne socket ct

            member _.DisposeAsync() : ValueTask =
                ValueTask(
                    task {
                        if not disposed then
                            disposed <- true

                            try
                                if
                                    socket.State = WebSocketState.Open
                                    || socket.State = WebSocketState.CloseReceived
                                then
                                    do!
                                        socket.CloseAsync(
                                            WebSocketCloseStatus.NormalClosure,
                                            "shutdown",
                                            CancellationToken.None
                                        )
                            with _ ->
                                ()

                            socket.Dispose()
                            sendLock.Dispose()
                    }
                )

    /// <summary>
    /// Client-side <see cref="ITransport"/> wrapping a connected
    /// <see cref="ClientWebSocket"/> (RFC §22). Reconnect logic is a
    /// higher-level concern and is not handled by this transport.
    /// </summary>
    type ClientWebSocketTransport(socket: ClientWebSocket) =
        let sendLock = new SemaphoreSlim(1, 1)
        let mutable disposed = false

        interface ITransport with
            member _.SendAsync(envelope, ct) = sendOne socket sendLock envelope ct
            member _.ReceiveAsync(ct) = receiveOne socket ct

            member _.DisposeAsync() : ValueTask =
                ValueTask(
                    task {
                        if not disposed then
                            disposed <- true

                            try
                                if socket.State = WebSocketState.Open then
                                    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 2.0)

                                    do!
                                        socket.CloseOutputAsync(
                                            WebSocketCloseStatus.NormalClosure,
                                            "shutdown",
                                            cts.Token
                                        )
                            with _ ->
                                ()

                            socket.Dispose()
                            sendLock.Dispose()
                    }
                )

        /// <summary>Connect a <see cref="ClientWebSocket"/> to <paramref name="uri"/> with optional headers (RFC §22).</summary>
        static member ConnectAsync
            (uri: Uri, ?headers: IDictionary<string, string>, ?ct: CancellationToken)
            : Task<ClientWebSocketTransport> =
            task {
                let ct = defaultArg ct CancellationToken.None
                let socket = new ClientWebSocket()

                match headers with
                | Some hs ->
                    for kv in hs do
                        socket.Options.SetRequestHeader(kv.Key, kv.Value)
                | None -> ()

                do! socket.ConnectAsync(uri, ct)
                return new ClientWebSocketTransport(socket)
            }

    /// <summary>Server configuration: listen URL and per-connection callback (RFC §22).</summary>
    type WebSocketServerOptions =
        {
            /// <summary>HTTP base URL (e.g. <c>http://127.0.0.1:0/</c>). Port 0 binds an ephemeral port.</summary>
            Url: string
            /// <summary>Invoked once per accepted WebSocket connection.</summary>
            OnConnection: ITransport -> Task
        }

    /// <summary>
    /// Convert an HTTP base URI returned by <see cref="runServerAsync"/> into a
    /// <c>ws://</c>/<c>wss://</c> URI suitable for
    /// <see cref="ClientWebSocketTransport.ConnectAsync"/>.
    /// </summary>
    let toWebSocketUri (httpBase: Uri) (path: string) : Uri =
        let scheme =
            match httpBase.Scheme with
            | "https" -> "wss"
            | _ -> "ws"

        let builder = UriBuilder(httpBase)
        builder.Scheme <- scheme
        builder.Path <- path
        builder.Uri

    /// <summary>
    /// Start an ASP.NET Core host accepting WebSocket connections at
    /// <c>/ws</c>. Returns a disposer that stops the host and the bound
    /// HTTP base URI (RFC §22).
    /// </summary>
    let runServerAsync (options: WebSocketServerOptions) (ct: CancellationToken) : Task<IAsyncDisposable * Uri> =
        task {
            let builder = WebApplication.CreateBuilder()
            builder.Logging.ClearProviders() |> ignore
            let app = builder.Build()
            app.Urls.Clear()
            app.Urls.Add options.Url
            app.UseWebSockets() |> ignore

            let handler =
                Func<HttpContext, Task>(fun ctx ->
                    task {
                        if ctx.WebSockets.IsWebSocketRequest then
                            let! ws = ctx.WebSockets.AcceptWebSocketAsync()
                            let transport = new ServerWebSocketTransport(ws) :> ITransport

                            try
                                do! options.OnConnection transport
                            with _ ->
                                ()
                        else
                            ctx.Response.StatusCode <- 400
                    }
                    :> Task)

            app.Map(PathString "/ws", handler) |> ignore

            do! app.StartAsync(ct)

            let server = app.Services.GetRequiredService<IServer>()

            let first =
                match server.Features.Get<IServerAddressesFeature>() with
                | null -> failwith "WebSocket server has no IServerAddressesFeature"
                | feat ->
                    feat.Addresses
                    |> Seq.tryHead
                    |> Option.defaultWith (fun () -> failwith "WebSocket server did not bind any address")

            let uri = Uri(first.TrimEnd('/') + "/")

            let disposer =
                { new IAsyncDisposable with
                    member _.DisposeAsync() =
                        ValueTask(
                            task {
                                try
                                    use stopCts = new CancellationTokenSource(TimeSpan.FromSeconds 2.0)
                                    do! app.StopAsync(stopCts.Token)
                                with _ ->
                                    ()

                                try
                                    do! (app :> IAsyncDisposable).DisposeAsync()
                                with _ ->
                                    ()
                            }
                        )
                }

            return disposer, uri
        }
