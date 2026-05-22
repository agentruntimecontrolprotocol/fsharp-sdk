namespace ARCP.Core

open System
open System.Text.Json

/// Encode/decode `Message` values into wire envelopes.
///
/// The envelope payload is the per-case payload record serialised
/// to a `JsonElement`. Decoding reads the envelope eagerly to get
/// `type`, then decodes `payload` lazily against the matching
/// payload record. Unknown `type` strings produce an
/// `InvalidRequest` error.
[<RequireQualifiedAccess>]
module Codec =

    let private payloadElement (value: 'T) : JsonElement = Json.serializeToElement<'T> value

    let private decodePayload<'T> (env: Envelope) : 'T = Json.deserializeElement<'T> env.Payload

    /// Build an envelope around a `Message`. Caller may further
    /// decorate with session/trace/job ids before send.
    let toEnvelope (msg: Message) : Envelope =
        let payload =
            match msg with
            | Message.SessionHello p -> payloadElement p
            | Message.SessionWelcome p -> payloadElement p
            | Message.SessionPing p -> payloadElement p
            | Message.SessionPong p -> payloadElement p
            | Message.SessionAck p -> payloadElement p
            | Message.SessionListJobs p -> payloadElement p
            | Message.SessionJobs p -> payloadElement p
            | Message.SessionBye p -> payloadElement p
            | Message.SessionError p -> payloadElement p
            | Message.JobSubmit p -> payloadElement p
            | Message.JobAccepted p -> payloadElement p
            | Message.JobEvent p -> payloadElement p
            | Message.JobResult p -> payloadElement p
            | Message.JobError p -> payloadElement p
            | Message.JobCancel p -> payloadElement p
            | Message.JobSubscribe p -> payloadElement p
            | Message.JobSubscribed p -> payloadElement p
            | Message.JobUnsubscribe p -> payloadElement p

        Envelope.create (Message.typeOf msg) payload

    /// Decode an envelope's `payload` to its `Message` representation.
    let toMessage (env: Envelope) : Result<Message, ARCPError> =
        try
            match env.Type with
            | "session.hello" -> Ok(Message.SessionHello(decodePayload env))
            | "session.welcome" -> Ok(Message.SessionWelcome(decodePayload env))
            | "session.ping" -> Ok(Message.SessionPing(decodePayload env))
            | "session.pong" -> Ok(Message.SessionPong(decodePayload env))
            | "session.ack" -> Ok(Message.SessionAck(decodePayload env))
            | "session.list_jobs" -> Ok(Message.SessionListJobs(decodePayload env))
            | "session.jobs" -> Ok(Message.SessionJobs(decodePayload env))
            | "session.bye" -> Ok(Message.SessionBye(decodePayload env))
            | "session.error" -> Ok(Message.SessionError(decodePayload env))
            | "job.submit" -> Ok(Message.JobSubmit(decodePayload env))
            | "job.accepted" -> Ok(Message.JobAccepted(decodePayload env))
            | "job.event" -> Ok(Message.JobEvent(decodePayload env))
            | "job.result" -> Ok(Message.JobResult(decodePayload env))
            | "job.error" -> Ok(Message.JobError(decodePayload env))
            | "job.cancel" -> Ok(Message.JobCancel(decodePayload env))
            | "job.subscribe" -> Ok(Message.JobSubscribe(decodePayload env))
            | "job.subscribed" -> Ok(Message.JobSubscribed(decodePayload env))
            | "job.unsubscribe" -> Ok(Message.JobUnsubscribe(decodePayload env))
            | other -> Error(ARCPError.InvalidRequest(sprintf "Unknown message type: %s" other, None))
        with :? JsonException as ex ->
            Error(ARCPError.InvalidRequest(sprintf "Malformed payload for %s: %s" env.Type ex.Message, None))

    /// Serialise an envelope to a JSON string for the wire.
    let writeEnvelope (env: Envelope) : string = Json.serialize env

    /// Parse a JSON string from the wire into an envelope.
    let readEnvelope (json: string) : Result<Envelope, ARCPError> =
        try
            Ok(Json.deserialize<Envelope> json)
        with :? JsonException as ex ->
            Error(ARCPError.InvalidRequest(sprintf "Malformed envelope: %s" ex.Message, None))
