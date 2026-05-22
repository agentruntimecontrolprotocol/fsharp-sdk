namespace ARCP.Core

open System.Text.Json

/// The wire envelope (spec §5.1, §6, §7, §8).
///
/// Eight fields. Payload is decoded lazily so unknown `type` values
/// round-trip without forcing a schema decode (spec §5.1: "MUST
/// ignore unknown top-level envelope fields"). This extends to
/// unknown `type` strings.
type Envelope =
    {
        Arcp: string
        Id: string
        Type: string
        SessionId: string option
        TraceId: string option
        JobId: string option
        EventSeq: int64 option
        Payload: JsonElement
    }

[<RequireQualifiedAccess>]
module Envelope =
    /// Build an envelope with the protocol version pinned to the
    /// current SDK and a freshly minted message id.
    let create (envType: string) (payload: JsonElement) : Envelope =
        {
            Arcp = Version.Protocol
            Id = (MessageId.newId ()).Value
            Type = envType
            SessionId = None
            TraceId = None
            JobId = None
            EventSeq = None
            Payload = payload
        }

    let withSessionId (sid: SessionId) (env: Envelope) : Envelope = { env with SessionId = Some sid.Value }

    let withTraceId (tid: TraceId) (env: Envelope) : Envelope = { env with TraceId = Some tid.Value }

    let withJobId (jid: JobId) (env: Envelope) : Envelope = { env with JobId = Some jid.Value }

    let withEventSeq (seq: int64) (env: Envelope) : Envelope = { env with EventSeq = Some seq }

    let withId (id: string) (env: Envelope) : Envelope = { env with Id = id }

    let sessionIdOpt (env: Envelope) : SessionId option =
        env.SessionId |> Option.map SessionId.ofString

    let jobIdOpt (env: Envelope) : JobId option = env.JobId |> Option.map JobId.ofString
