namespace ARCP.Runtime

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open FSharp.Control
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Streaming
open ARCP.Messages.Execution
open ARCP.Messages.Registry

/// <summary>
/// Streaming subsystem (RFC §11, §12). Streams are unidirectional, per-stream
/// sequenced, and bounded by a <c>Channel.CreateBounded</c> queue to provide
/// natural backpressure to writers.
/// </summary>
module Stream =

    /// <summary>
    /// Side-band record describing an outgoing stream's sequencing state.
    /// </summary>
    type internal OutgoingStreamState =
        {
            StreamId: StreamId
            mutable Sequence: int
            mutable Capacity: int
            Channel: Channel<StreamChunk>
            Kind: StreamKind
            mutable Closed: bool
        }

    /// <summary>
    /// Side-band record describing an incoming stream's expected sequence.
    /// </summary>
    type internal IncomingStreamState =
        {
            StreamId: StreamId
            mutable Expected: int
            Channel: Channel<Result<StreamChunk, ARCPError>>
            Kind: StreamKind
        }

/// <summary>
/// Writer handle for an outgoing stream. <see cref="WriteChunkAsync"/> awaits
/// channel capacity, providing natural backpressure to producers.
/// </summary>
type StreamWriter internal (streamId: StreamId, send: Envelope<MessageType> -> Task, state: Stream.OutgoingStreamState)
    =

    /// <summary>The id of this stream.</summary>
    member _.StreamId: StreamId = streamId

    /// <summary>
    /// Enqueue a chunk for sending. Blocks (asynchronously) when the channel
    /// is full, applying backpressure.
    /// </summary>
    member _.WriteChunkAsync(data: JsonElement, ?ct: CancellationToken) : Task<Result<unit, ARCPError>> =
        let ct = defaultArg ct CancellationToken.None

        task {
            if state.Closed then
                return Error(FailedPrecondition(sprintf "stream %A closed" streamId))
            else
                try
                    let seq = Interlocked.Increment(&state.Sequence)

                    let chunk: StreamChunk =
                        {
                            Sequence = seq
                            Data = data
                            Sha256 = None
                        }

                    do! state.Channel.Writer.WriteAsync(chunk, ct).AsTask()

                    let env = Envelopes.streamChunk chunk |> Envelope.withStream streamId

                    do! send env
                    return Ok()
                with
                | :? OperationCanceledException -> return Error(Cancelled "write cancelled")
                | ex -> return Error(Internal(ex.Message, Some ex))
        }

    /// <summary>Emit <c>stream.close</c> and stop accepting further chunks.</summary>
    member _.CloseAsync(?reason: string, ?ct: CancellationToken) : Task =
        let _ct = defaultArg ct CancellationToken.None

        task {
            if not state.Closed then
                state.Closed <- true
                state.Channel.Writer.TryComplete() |> ignore

                let env = Envelopes.streamClose { Reason = reason } |> Envelope.withStream streamId

                do! send env

            return ()
        }

    /// <summary>Emit <c>stream.error</c> and stop accepting further chunks.</summary>
    member _.ErrorAsync(err: ARCPError, ?ct: CancellationToken) : Task =
        let _ct = defaultArg ct CancellationToken.None

        task {
            if not state.Closed then
                state.Closed <- true
                state.Channel.Writer.TryComplete() |> ignore

                let payload: StreamError =
                    {
                        Code = ARCPError.code err
                        Message = ARCPError.message err
                        Retryable = Some(ARCPError.retryable err)
                        Details = None
                        Cause = None
                        TraceId = None
                    }

                let env = Envelopes.streamError payload |> Envelope.withStream streamId

                do! send env

            return ()
        }

/// <summary>
/// Reader handle for an incoming stream. <see cref="ReadAllAsync"/> exposes
/// the chunks as an <see cref="IAsyncEnumerable{T}"/>; out-of-order or other
/// stream errors materialize as exceptions in the enumerable.
/// </summary>
type StreamReader internal (streamId: StreamId, state: Stream.IncomingStreamState) =

    /// <summary>The id of this stream.</summary>
    member _.StreamId: StreamId = streamId

    /// <summary>
    /// Read all chunks until the stream is closed. Out-of-order chunks or
    /// stream errors raise an exception through the enumerable.
    /// </summary>
    member _.ReadAllAsync(ct: CancellationToken) : IAsyncEnumerable<StreamChunk> =
        taskSeq {
            let mutable running = true

            while running do
                let! has = state.Channel.Reader.WaitToReadAsync(ct)

                if not has then
                    running <- false
                else
                    match state.Channel.Reader.TryRead() with
                    | true, Ok chunk -> yield chunk
                    | true, Error e ->
                        running <- false
                        raise (InvalidOperationException(ARCPError.message e))
                    | _ -> ()
        }

/// <summary>
/// Stream manager: tracks outgoing writers and incoming readers, dispatches
/// inbound <c>stream.*</c> envelopes, and enforces per-stream sequence
/// monotonicity.
/// </summary>
type StreamManager(send: Envelope<MessageType> -> Task, capacity: int) =

    let outgoing = ConcurrentDictionary<StreamId, Stream.OutgoingStreamState>()
    let incoming = ConcurrentDictionary<StreamId, Stream.IncomingStreamState>()

    /// <summary>
    /// Open a new outgoing stream. Emits <c>stream.open</c> and returns a
    /// writer whose channel respects <paramref name="capacity"/>.
    /// </summary>
    member _.OpenWriterAsync
        (kind: StreamKind, jobId: JobId option, ?contentType: string, ?encoding: string, ?ct: CancellationToken)
        : Task<StreamWriter> =
        let _ct = defaultArg ct CancellationToken.None

        task {
            let sid = StreamId.create ()

            let opts = BoundedChannelOptions(capacity)
            opts.FullMode <- BoundedChannelFullMode.Wait
            opts.SingleReader <- true
            opts.SingleWriter <- true
            let ch = Channel.CreateBounded<StreamChunk>(opts)

            let state: Stream.OutgoingStreamState =
                {
                    StreamId = sid
                    Sequence = 0
                    Capacity = capacity
                    Channel = ch
                    Kind = kind
                    Closed = false
                }

            outgoing.[sid] <- state

            let openPayload: StreamOpen =
                {
                    Kind = kind
                    ContentType = contentType
                    Encoding = encoding
                }

            let mutable env = Envelopes.streamOpen openPayload |> Envelope.withStream sid

            match jobId with
            | Some jid -> env <- env |> Envelope.withJob jid
            | None -> ()

            do! send env
            return StreamWriter(sid, send, state)
        }

    /// <summary>
    /// Register a reader for an incoming stream id. Subsequent
    /// <c>stream.chunk</c>/<c>stream.close</c>/<c>stream.error</c> envelopes
    /// for this id are routed to the returned reader.
    /// </summary>
    member _.RegisterIncoming(streamId: StreamId, kind: StreamKind) : StreamReader =
        let ch = Channel.CreateUnbounded<Result<StreamChunk, ARCPError>>()

        let state: Stream.IncomingStreamState =
            {
                StreamId = streamId
                Expected = 1
                Channel = ch
                Kind = kind
            }

        incoming.[streamId] <- state
        StreamReader(streamId, state)

    /// <summary>Dispatch a stream-related envelope to the relevant reader.</summary>
    member this.HandleAsync(env: Envelope<MessageType>) : Task<unit> =
        task {
            match env.StreamId with
            | None -> return ()
            | Some sid ->
                match env.Payload with
                | StreamOpen p ->
                    // auto-register if not already registered
                    if not (incoming.ContainsKey sid) then
                        this.RegisterIncoming(sid, p.Kind) |> ignore

                    return ()
                | StreamChunk chunk ->
                    match incoming.TryGetValue sid with
                    | true, state ->
                        if chunk.Sequence <> state.Expected then
                            let err =
                                FailedPrecondition(
                                    sprintf "stream %A: expected seq %d, got %d" sid state.Expected chunk.Sequence
                                )

                            let! _ = state.Channel.Writer.WriteAsync(Error err).AsTask()
                            state.Channel.Writer.TryComplete() |> ignore
                            incoming.TryRemove sid |> ignore
                        else
                            state.Expected <- state.Expected + 1
                            let! _ = state.Channel.Writer.WriteAsync(Ok chunk).AsTask()
                            ()
                    | _ -> ()

                    return ()
                | StreamClose _ ->
                    match incoming.TryGetValue sid with
                    | true, state ->
                        state.Channel.Writer.TryComplete() |> ignore
                        incoming.TryRemove sid |> ignore
                    | _ -> ()

                    return ()
                | StreamError ep ->
                    match incoming.TryGetValue sid with
                    | true, state ->
                        let err = Internal(ep.Message, None)
                        let! _ = state.Channel.Writer.WriteAsync(Error err).AsTask()
                        state.Channel.Writer.TryComplete() |> ignore
                        incoming.TryRemove sid |> ignore
                    | _ -> ()

                    return ()
                | _ -> return ()
        }

    /// <summary>
    /// Apply backpressure to an outgoing stream. The desired rate is recorded
    /// for use by the writer; bounded-channel capacity provides the actual
    /// flow-control mechanism.
    /// </summary>
    member _.ApplyBackpressure(streamId: StreamId, desiredRatePerSecond: int) : unit =
        match outgoing.TryGetValue streamId with
        | true, state -> state.Capacity <- max 1 desiredRatePerSecond
        | _ -> ()
