module ARCP.UnitTests.WebSocketTransportTests

open System
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport

/// Fake WebSocket that records concurrent send overlaps and serves a
/// scripted sequence of incoming text frames.
type private FakeWebSocket(incoming: byte[] list, sendDelayMs: int) =
    inherit WebSocket()
    let queue = ConcurrentQueue<byte[]>(incoming)
    let mutable inFlightSends = 0
    let mutable maxInFlight = 0
    let mutable totalSends = 0
    let sendOrder = ConcurrentQueue<string>()
    let mutable state = WebSocketState.Open

    member _.MaxInFlightSends = maxInFlight
    member _.TotalSends = totalSends
    member _.SendOrder = sendOrder |> Seq.toArray

    override _.State = state
    override _.CloseStatus = Nullable()
    override _.CloseStatusDescription = ""
    override _.SubProtocol = ""

    override this.Abort() = state <- WebSocketState.Aborted
    override this.Dispose() = state <- WebSocketState.Closed

    override this.CloseAsync(_, _, _) =
        state <- WebSocketState.Closed
        Task.CompletedTask

    override this.CloseOutputAsync(_, _, _) =
        state <- WebSocketState.CloseSent
        Task.CompletedTask

    override this.SendAsync
        (
            buffer: ArraySegment<byte>,
            _msgType: WebSocketMessageType,
            _endOfMessage: bool,
            ct: CancellationToken
        ) =
        task {
            let now = Interlocked.Increment(&inFlightSends)

            let rec bumpMax () =
                let current = Volatile.Read(&maxInFlight)

                if now > current then
                    if Interlocked.CompareExchange(&maxInFlight, now, current) <> current then
                        bumpMax ()

            bumpMax ()
            Interlocked.Increment(&totalSends) |> ignore
            let copy = Array.zeroCreate<byte> buffer.Count
            Array.blit buffer.Array buffer.Offset copy 0 buffer.Count
            sendOrder.Enqueue(Encoding.UTF8.GetString copy)
            do! Task.Delay(sendDelayMs, ct)
            Interlocked.Decrement(&inFlightSends) |> ignore
        }
        :> Task

    override this.ReceiveAsync(buffer: ArraySegment<byte>, ct: CancellationToken) =
        task {
            match queue.TryDequeue() with
            | true, frame ->
                Array.blit frame 0 buffer.Array buffer.Offset frame.Length
                return WebSocketReceiveResult(frame.Length, WebSocketMessageType.Text, true)
            | _ ->
                state <- WebSocketState.CloseReceived
                return WebSocketReceiveResult(0, WebSocketMessageType.Close, true)
        }

let private envelope id =
    let payload: SessionPingPayload =
        {
            Nonce = "n"
            SentAt = DateTimeOffset.UnixEpoch
        }

    Envelope.create "session.ping" (Json.serializeToElement payload)
    |> Envelope.withId id

[<Fact>]
let ``WebSocketClientTransport serialises concurrent sends`` () =
    let fake = new FakeWebSocket([], 50)
    let transport: ITransport = new WebSocketClientTransport(fake, ownsSocket = false)

    let tasks =
        [| for i in 1..5 -> transport.SendAsync(envelope (sprintf "id-%d" i), CancellationToken.None) |]

    Task.WhenAll(tasks).Wait()
    fake.TotalSends |> should equal 5
    // Critical assertion for the fix: no two SendAsync calls ever
    // overlap on the underlying WebSocket.
    fake.MaxInFlightSends |> should equal 1

[<Fact>]
let ``WebSocketClientTransport preserves the order callers invoked SendAsync`` () =
    let fake = new FakeWebSocket([], 5)
    let transport: ITransport = new WebSocketClientTransport(fake, ownsSocket = false)
    let sent = ResizeArray<Task>()

    // Sequential awaits — each Send completes before the next begins.
    for i in 1..3 do
        let t = transport.SendAsync(envelope (sprintf "id-%d" i), CancellationToken.None)
        sent.Add t
        t.Wait()

    let order = fake.SendOrder
    order.Length |> should equal 3
    order.[0].Contains "id-1" |> should equal true
    order.[1].Contains "id-2" |> should equal true
    order.[2].Contains "id-3" |> should equal true

[<Fact>]
let ``WebSocketClientTransport CloseAsync transitions state and is safe to call twice`` () =
    let fake = new FakeWebSocket([], 0)
    let transport: ITransport = new WebSocketClientTransport(fake, ownsSocket = false)
    transport.CloseAsync(CancellationToken.None).Wait()
    // Second close is a no-op (no throw).
    transport.CloseAsync(CancellationToken.None).Wait()

[<Fact>]
let ``WebSocketClientTransport reads framed envelopes`` () =
    let bytes (s: string) = Encoding.UTF8.GetBytes s
    let env1 = Codec.writeEnvelope (envelope "in-1")
    let env2 = Codec.writeEnvelope (envelope "in-2")
    let fake = new FakeWebSocket([ bytes env1; bytes env2 ], 0)
    let transport: ITransport = new WebSocketClientTransport(fake, ownsSocket = false)
    let enumerable = transport.Receive(CancellationToken.None)
    let enumerator = enumerable.GetAsyncEnumerator(CancellationToken.None)
    let collected = ResizeArray<Envelope>()

    try
        let mutable more = true

        while more do
            let next = enumerator.MoveNextAsync().AsTask()

            if next.Result then
                collected.Add enumerator.Current
            else
                more <- false
    finally
        ignore (enumerator.DisposeAsync().AsTask())

    collected.Count |> should equal 2
    collected.[0].Id |> should equal "in-1"
    collected.[1].Id |> should equal "in-2"

[<Fact>]
let ``WebSocketCloseCodes maps ARCPError to RFC 6455 codes`` () =
    WebSocketCloseCodes.ofError ARCPError.HeartbeatLost
    |> should equal WebSocketCloseCodes.internalError

    WebSocketCloseCodes.ofError (ARCPError.InvalidRequest("x", None))
    |> should equal WebSocketCloseCodes.protocolError

    WebSocketCloseCodes.ofError (ARCPError.InternalError "x")
    |> should equal WebSocketCloseCodes.internalError

    WebSocketCloseCodes.ofError (ARCPError.JobNotFound "x")
    |> should equal WebSocketCloseCodes.normal
