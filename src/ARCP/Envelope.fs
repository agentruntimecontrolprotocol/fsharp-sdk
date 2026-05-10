namespace ARCP

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open ARCP.Ids

/// <summary>
/// Canonical ARCP envelope (RFC §6.1). Every protocol message uses this shape;
/// the typed payload lives in <c>Payload</c>, which is generic so the same
/// record can carry any of the variant payload records.
/// </summary>
module Envelope =

    /// <summary>Message priority (RFC §6.5).</summary>
    type Priority =
        | [<JsonName("low")>] Low
        | [<JsonName("normal")>] Normal
        | [<JsonName("high")>] High
        | [<JsonName("critical")>] Critical

    [<RequireQualifiedAccess>]
    module Priority =
        let value =
            function
            | Low -> "low"
            | Normal -> "normal"
            | High -> "high"
            | Critical -> "critical"

    /// <summary>
    /// Generic envelope record. Most callers will use the concrete
    /// <c>Envelope&lt;MessageType&gt;</c> form once <c>MessageType</c> is
    /// defined in <c>Messages/Registry.fs</c>.
    /// </summary>
    type Envelope<'Payload> =
        {
            /// <summary>Protocol version understood by the sender (RFC §6.1.1).</summary>
            [<JsonPropertyName("arcp")>]
            Arcp: string
            /// <summary>Globally unique message id; transport idempotency key.</summary>
            Id: MessageId
            /// <summary>Wire <c>type</c> string (e.g. <c>job.progress</c>).</summary>
            Type: string
            /// <summary>RFC 3339 timestamp of the sender.</summary>
            Timestamp: DateTimeOffset
            Source: string option
            Target: string option
            SessionId: SessionId option
            JobId: JobId option
            StreamId: StreamId option
            SubscriptionId: SubscriptionId option
            TraceId: TraceId option
            SpanId: SpanId option
            ParentSpanId: SpanId option
            CorrelationId: MessageId option
            CausationId: MessageId option
            IdempotencyKey: IdempotencyKey option
            Priority: Priority option
            Extensions: JsonObject option
            Payload: 'Payload
        }

    [<RequireQualifiedAccess>]
    module Envelope =

        /// <summary>
        /// Construct a minimal envelope with required fields and the supplied payload.
        /// Optional fields are <c>None</c>.
        /// </summary>
        let create (envType: string) (payload: 'P) : Envelope<'P> =
            {
                Arcp = ARCP.Version.Protocol
                Id = MessageId.create ()
                Type = envType
                Timestamp = DateTimeOffset.UtcNow
                Source = None
                Target = None
                SessionId = None
                JobId = None
                StreamId = None
                SubscriptionId = None
                TraceId = None
                SpanId = None
                ParentSpanId = None
                CorrelationId = None
                CausationId = None
                IdempotencyKey = None
                Priority = None
                Extensions = None
                Payload = payload
            }

        let withSession (sid: SessionId) (e: Envelope<'P>) : Envelope<'P> = { e with SessionId = Some sid }

        let withJob (jid: JobId) (e: Envelope<'P>) : Envelope<'P> = { e with JobId = Some jid }

        let withStream (sid: StreamId) (e: Envelope<'P>) : Envelope<'P> = { e with StreamId = Some sid }

        let withSubscription (sid: SubscriptionId) (e: Envelope<'P>) : Envelope<'P> =
            { e with SubscriptionId = Some sid }

        let withTrace (ctx: ARCP.Trace.TraceContext) (e: Envelope<'P>) : Envelope<'P> =
            { e with
                TraceId = Some ctx.TraceId
                SpanId = Some ctx.SpanId
                ParentSpanId = ctx.ParentSpanId
            }

        let withCorrelation (cid: MessageId) (e: Envelope<'P>) : Envelope<'P> = { e with CorrelationId = Some cid }

        let withCausation (cid: MessageId) (e: Envelope<'P>) : Envelope<'P> = { e with CausationId = Some cid }

        let withIdempotencyKey (k: IdempotencyKey) (e: Envelope<'P>) : Envelope<'P> = { e with IdempotencyKey = Some k }

        let withPriority (p: Priority) (e: Envelope<'P>) : Envelope<'P> = { e with Priority = Some p }

        let withExtensions (ext: JsonObject) (e: Envelope<'P>) : Envelope<'P> = { e with Extensions = Some ext }

        /// <summary>Map the payload while preserving all envelope metadata.</summary>
        let mapPayload (f: 'A -> 'B) (e: Envelope<'A>) : Envelope<'B> =
            {
                Arcp = e.Arcp
                Id = e.Id
                Type = e.Type
                Timestamp = e.Timestamp
                Source = e.Source
                Target = e.Target
                SessionId = e.SessionId
                JobId = e.JobId
                StreamId = e.StreamId
                SubscriptionId = e.SubscriptionId
                TraceId = e.TraceId
                SpanId = e.SpanId
                ParentSpanId = e.ParentSpanId
                CorrelationId = e.CorrelationId
                CausationId = e.CausationId
                IdempotencyKey = e.IdempotencyKey
                Priority = e.Priority
                Extensions = e.Extensions
                Payload = f e.Payload
            }
