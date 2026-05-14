namespace ARCP.Core

open System

/// W3C Trace Context identifiers. Trace ids are 32 hex chars,
/// span ids 16 hex chars; the strings are kept opaque on the wire.

[<Struct>]
type TraceId = TraceId of string with
    member this.Value = let (TraceId v) = this in v
    override this.ToString() = this.Value

[<Struct>]
type SpanId = SpanId of string with
    member this.Value = let (SpanId v) = this in v
    override this.ToString() = this.Value

[<RequireQualifiedAccess>]
module TraceId =
    let private hex (count: int) =
        let buf = Array.zeroCreate<byte> count
        Random.Shared.NextBytes(buf)
        Convert.ToHexString(buf).ToLowerInvariant()

    let newId () : TraceId = TraceId(hex 16)
    let ofString (s: string) : TraceId = TraceId s

[<RequireQualifiedAccess>]
module SpanId =
    let private hex (count: int) =
        let buf = Array.zeroCreate<byte> count
        Random.Shared.NextBytes(buf)
        Convert.ToHexString(buf).ToLowerInvariant()

    let newId () : SpanId = SpanId(hex 8)
    let ofString (s: string) : SpanId = SpanId s
