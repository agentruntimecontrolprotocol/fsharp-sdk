namespace ARCP.Transport

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ARCP
open ARCP.Errors
open ARCP.Envelope
open ARCP.Messages
open ARCP.Messages.Registry

/// <summary>
/// Wire-level envelope shape used during serialization: the typed payload
/// is collapsed to a raw <see cref="JsonElement"/>. The Envelope's
/// <c>Type</c> field carries the wire <c>type</c> discriminator.
/// </summary>
type WireEnvelope = Envelope<JsonElement>

/// <summary>
/// Bidirectional ARCP transport. Implementations are responsible for
/// framing and ordering; the runtime/client consume
/// <c>Envelope&lt;MessageType&gt;</c>.
/// </summary>
type ITransport =
    inherit IAsyncDisposable
    /// <summary>Send an envelope. Returns when the transport has accepted it.</summary>
    abstract SendAsync: envelope: Envelope<MessageType> * ct: CancellationToken -> Task
    /// <summary>Receive the next envelope. <c>None</c> indicates the peer closed.</summary>
    abstract ReceiveAsync: ct: CancellationToken -> Task<Envelope<MessageType> option>

/// <summary>Envelope/wire conversion helpers (RFC §6.1).</summary>
module Transport =

    /// <summary>Project a typed envelope to its wire form.</summary>
    let toWire (env: Envelope<MessageType>) : WireEnvelope =
        let payloadEl = toPayloadElement env.Payload

        {
            Arcp = env.Arcp
            Id = env.Id
            Type = wireType env.Payload
            Timestamp = env.Timestamp
            Source = env.Source
            Target = env.Target
            SessionId = env.SessionId
            JobId = env.JobId
            StreamId = env.StreamId
            SubscriptionId = env.SubscriptionId
            TraceId = env.TraceId
            SpanId = env.SpanId
            ParentSpanId = env.ParentSpanId
            CorrelationId = env.CorrelationId
            CausationId = env.CausationId
            IdempotencyKey = env.IdempotencyKey
            Priority = env.Priority
            Extensions = env.Extensions
            Payload = payloadEl
        }

    /// <summary>Decode a wire envelope to its typed form.</summary>
    let fromWire (wire: WireEnvelope) : Result<Envelope<MessageType>, ARCPError> =
        match ofWireType wire.Type wire.Payload with
        | Ok msg ->
            Ok
                {
                    Arcp = wire.Arcp
                    Id = wire.Id
                    Type = wire.Type
                    Timestamp = wire.Timestamp
                    Source = wire.Source
                    Target = wire.Target
                    SessionId = wire.SessionId
                    JobId = wire.JobId
                    StreamId = wire.StreamId
                    SubscriptionId = wire.SubscriptionId
                    TraceId = wire.TraceId
                    SpanId = wire.SpanId
                    ParentSpanId = wire.ParentSpanId
                    CorrelationId = wire.CorrelationId
                    CausationId = wire.CausationId
                    IdempotencyKey = wire.IdempotencyKey
                    Priority = wire.Priority
                    Extensions = wire.Extensions
                    Payload = msg
                }
        | Error e -> Error e

    /// <summary>Serialize an envelope to a JSON string.</summary>
    let serializeEnvelope (env: Envelope<MessageType>) : string = Json.serialize (toWire env)

    /// <summary>Parse a JSON string into a typed envelope.</summary>
    let parseEnvelope (json: string) : Result<Envelope<MessageType>, ARCPError> =
        try
            let wire = Json.deserialize<WireEnvelope> json
            fromWire wire
        with ex ->
            Error(InvalidArgument("envelope", ex.Message))
