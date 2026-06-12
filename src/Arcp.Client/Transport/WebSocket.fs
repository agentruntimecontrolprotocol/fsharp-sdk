namespace ARCP.Client.Transport

open System
open System.Buffers
open System.Collections.Generic
open System.IO
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client

/// Client-side WebSocket transport using the BCL
/// `System.Net.WebSockets.ClientWebSocket`. Spec §4 requires
/// WebSocket for network deployments.
///
/// One text frame per envelope. The receive loop reassembles
/// continuation frames into a single message.
type WebSocketClientTransport(socket: WebSocket, ownsSocket: bool, maxMessageBytes: int64) =
    // The BCL WebSocket allows only one outstanding send at a time,
    // so we serialise concurrent callers (auto-ack, pong, submit,
    // cancel, close) through an async-aware semaphore — a plain
    // `lock` only covers the synchronous call to `SendAsync` and
    // would release before the returned Task completes.
    let sendLock = new SemaphoreSlim(1, 1)
    let mutable closed = false

    let sendOne (env: Envelope) (ct: CancellationToken) : Task =
        task {
            let json = Codec.writeEnvelope env
            let bytes = Encoding.UTF8.GetBytes json
            do! sendLock.WaitAsync ct

            try
                do! socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
            finally
                sendLock.Release() |> ignore
        }
        :> Task

    let rec receiveOne (ct: CancellationToken) : Task<Envelope option> =
        task {
            let buffer = ArrayPool<byte>.Shared.Rent(8192)

            let! frame =
                task {
                    try
                        use ms = new MemoryStream()
                        // Mutation here drives WebSocket frame reassembly:
                        // BCL `ReceiveAsync` returns one frame at a time and
                        // there is no functional aggregator for it.
                        let mutable endOfMessage = false
                        let mutable closedRemotely = false

                        while not endOfMessage && not closedRemotely do
                            let! result = socket.ReceiveAsync(ArraySegment<byte>(buffer), ct)

                            if result.MessageType = WebSocketMessageType.Close then
                                closedRemotely <- true
                            else
                                if ms.Length + int64 result.Count > maxMessageBytes then
                                    // §66: reject oversized messages before the
                                    // codec ever sees them.
                                    raise (
                                        WebSocketException(
                                            WebSocketError.HeaderError,
                                            sprintf "Inbound message exceeded %d bytes" maxMessageBytes
                                        )
                                    )

                                ms.Write(buffer, 0, result.Count)
                                endOfMessage <- result.EndOfMessage

                        if closedRemotely then
                            return Choice1Of2()
                        else
                            return Choice2Of2(Encoding.UTF8.GetString(ms.ToArray()))
                    finally
                        ArrayPool<byte>.Shared.Return(buffer)
                }

            match frame with
            | Choice1Of2() -> return None
            | Choice2Of2 text ->
                match Codec.readEnvelope text with
                | Ok env -> return Some env
                // Malformed envelopes are dropped at the transport boundary
                // per spec §4 — clients must not surface them to dispatch.
                | Error _ -> return! receiveOne ct
        }

    /// Defaults to a 256 MiB cap on a single reassembled message to
    /// bound memory against a peer streaming continuation frames
    /// indefinitely.
    new(socket: WebSocket, ownsSocket: bool) = WebSocketClientTransport(socket, ownsSocket, 256L * 1024L * 1024L)

    interface ITransport with
        member _.SendAsync(env, ct) = sendOne env ct

        member _.Receive(ct) =
            { new IAsyncEnumerable<Envelope> with
                member _.GetAsyncEnumerator(c) =
                    let linked = CancellationTokenSource.CreateLinkedTokenSource(c, ct)
                    let mutable current = Unchecked.defaultof<Envelope>
                    let mutable finished = false

                    { new IAsyncEnumerator<Envelope> with
                        member _.Current = current

                        member _.MoveNextAsync() =
                            task {
                                if finished || closed then
                                    return false
                                else
                                    try
                                        let! r = receiveOne linked.Token

                                        match r with
                                        | None ->
                                            finished <- true
                                            return false
                                        | Some env ->
                                            current <- env
                                            return true
                                    with
                                    | :? OperationCanceledException ->
                                        finished <- true
                                        return false
                                    | :? WebSocketException ->
                                        finished <- true
                                        return false
                            }
                            |> ValueTask<bool>

                        member _.DisposeAsync() =
                            linked.Dispose()
                            ValueTask.CompletedTask
                    }
            }

        member _.CloseAsync(ct) =
            task {
                closed <- true

                try
                    if
                        socket.State = WebSocketState.Open
                        || socket.State = WebSocketState.CloseReceived
                    then
                        do! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct)
                with _ ->
                    ()

                if ownsSocket then
                    try
                        socket.Dispose()
                    with _ ->
                        ()
            }
            :> Task

[<RequireQualifiedAccess>]
module WebSocketClientTransport =
    /// Connect a new client transport to `uri`. The bearer token (if
    /// provided) is added as the `Authorization` header on the
    /// upgrade request. That header is host-layer metadata only and
    /// does not authenticate the ARCP session — set
    /// `ArcpClientOptions.Auth = AuthScheme.Bearer token` for session
    /// auth (`ArcpClientOptions.defaults` sends `auth.scheme = "none"`).
    let connectAsync (uri: Uri) (bearerToken: string option) (ct: CancellationToken) : Task<ITransport> =
        task {
            let client = new ClientWebSocket()

            match bearerToken with
            | Some t -> client.Options.SetRequestHeader("Authorization", "Bearer " + t)
            | None -> ()

            do! client.ConnectAsync(uri, ct)
            return new WebSocketClientTransport(client, ownsSocket = true) :> ITransport
        }

/// Map ARCP errors → RFC 6455 WebSocket close codes.
[<RequireQualifiedAccess>]
module WebSocketCloseCodes =
    let normal: int = 1000
    let protocolError: int = 1002
    let internalError: int = 1011

    let ofError (e: ARCPError) : int =
        match e with
        | ARCPError.HeartbeatLost -> internalError
        | ARCPError.InvalidRequest _ -> protocolError
        | ARCPError.InternalError _ -> internalError
        | _ -> normal
