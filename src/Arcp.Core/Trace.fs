namespace ARCP.Core

open System
open System.Security.Cryptography

/// W3C Trace Context identifiers. Trace ids are 32 hex chars,
/// span ids 16 hex chars; the strings are kept opaque on the wire.

[<Struct>]
type TraceId =
    | TraceId of string

    member this.Value = let (TraceId v) = this in v
    override this.ToString() = this.Value

[<Struct>]
type SpanId =
    | SpanId of string

    member this.Value = let (SpanId v) = this in v
    override this.ToString() = this.Value

/// Cryptographically random hex string. Trace/span ids cross trust
/// boundaries, so guess-resistance matters (W3C Trace Context).
[<AutoOpen>]
module private TraceRandom =
    let cryptoHex (count: int) =
        let buf = Array.zeroCreate<byte> count
        RandomNumberGenerator.Fill(Span<byte>(buf))
        Convert.ToHexString(buf).ToLowerInvariant()

[<RequireQualifiedAccess>]
module TraceId =
    let newId () : TraceId = TraceId(cryptoHex 16)
    let ofString (s: string) : TraceId = TraceId s

[<RequireQualifiedAccess>]
module SpanId =
    let newId () : SpanId = SpanId(cryptoHex 8)
    let ofString (s: string) : SpanId = SpanId s
