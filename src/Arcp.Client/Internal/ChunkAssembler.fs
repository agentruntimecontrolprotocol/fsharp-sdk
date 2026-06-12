namespace ARCP.Client.Internal

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text
open ARCP.Core

/// Buffers in-order chunks of a streamed result (spec §8.4).
///
/// One assembler instance per `result_id`. Chunks MUST arrive in
/// `chunk_seq` order per spec §8.4; out-of-order arrivals raise
/// `InvalidRequest` and the caller is expected to terminate the job.
/// Default caps guarding against unbounded result streams (DoS).
[<RequireQualifiedAccess>]
module internal ChunkLimits =
    [<Literal>]
    let DefaultMaxBytes: int64 = 256L * 1024L * 1024L // 256 MiB

    [<Literal>]
    let DefaultMaxChunks: int = 1_000_000

type internal ChunkAssembler(maxBytes: int64, maxChunks: int) =
    let buffer = ResizeArray<byte[]>()
    let mutable expectedSeq: int64 = 0L
    let mutable totalBytes: int64 = 0L
    let mutable closed = false

    new() = ChunkAssembler(ChunkLimits.DefaultMaxBytes, ChunkLimits.DefaultMaxChunks)

    /// Append a chunk. Returns `Ok finished` where `finished` is
    /// `true` once a `more = false` chunk has arrived. A stream that
    /// exceeds the byte/chunk cap is rejected to bound memory (§8.4).
    member _.Append(chunkSeq: int64, data: string, encoding: ChunkEncoding, more: bool) : Result<bool, ARCPError> =
        if closed then
            Error(ARCPError.InvalidRequest("Chunk arrived after stream closed", None))
        elif chunkSeq <> expectedSeq then
            Error(
                ARCPError.InvalidRequest(sprintf "Out-of-order chunk: expected %d, got %d" expectedSeq chunkSeq, None)
            )
        elif int64 buffer.Count >= int64 maxChunks then
            Error(ARCPError.InvalidRequest(sprintf "Result stream exceeded max chunk count (%d)" maxChunks, None))
        else
            let bytesResult =
                try
                    let bytes =
                        match encoding with
                        | ChunkEncoding.Utf8 -> Encoding.UTF8.GetBytes data
                        | ChunkEncoding.Base64 -> Convert.FromBase64String data

                    Ok bytes
                with
                | :? FormatException as fx ->
                    Error(ARCPError.InvalidRequest(sprintf "Invalid base64 chunk: %s" fx.Message, None))
                | :? DecoderFallbackException as dx ->
                    Error(ARCPError.InvalidRequest(sprintf "Invalid utf-8 chunk: %s" dx.Message, None))

            match bytesResult with
            | Error e -> Error e
            | Ok bytes when totalBytes + int64 bytes.Length > maxBytes ->
                Error(ARCPError.InvalidRequest(sprintf "Result stream exceeded max byte budget (%d)" maxBytes, None))
            | Ok bytes ->
                buffer.Add bytes
                totalBytes <- totalBytes + int64 bytes.Length
                expectedSeq <- expectedSeq + 1L

                if not more then
                    closed <- true

                Ok closed

    /// Materialise the assembled bytes. Throws if the stream has
    /// not yet seen its terminating chunk.
    member _.ToArray() : byte[] =
        if not closed then
            invalidOp "Chunk stream not yet terminated"

        let total = buffer |> Seq.sumBy (fun b -> b.Length)
        let result = Array.zeroCreate<byte> total
        let mutable offset = 0

        for b in buffer do
            Array.blit b 0 result offset b.Length
            offset <- offset + b.Length

        result

    member _.IsClosed = closed

/// Index of assemblers by `result_id`. Used by the client to track
/// multiple in-flight result streams.
type internal ChunkAssemblerIndex() =
    let assemblers = ConcurrentDictionary<string, ChunkAssembler>()

    member _.GetOrCreate(resultId: string) : ChunkAssembler =
        assemblers.GetOrAdd(resultId, fun _ -> ChunkAssembler())

    member _.Remove(resultId: string) : bool =
        match assemblers.TryRemove resultId with
        | true, _ -> true
        | _ -> false

    member _.TryGet(resultId: string) : ChunkAssembler option =
        match assemblers.TryGetValue resultId with
        | true, a -> Some a
        | _ -> None
