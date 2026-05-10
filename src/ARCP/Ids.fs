namespace ARCP

open System

/// <summary>
/// Strongly-typed identifiers as single-case discriminated unions (newtypes).
/// Mixing different id types is a compile error.
///
/// Each id is generated as a ULID (RFC §6.1.1 transport idempotency requires
/// globally-unique ids; ULIDs are lexicographically sortable so debugging is
/// easier than UUIDs).
///
/// FSharp.SystemTextJson is configured (see <see cref="ARCP.Json"/>) to
/// serialize single-case DUs as their bare inner string, so these are
/// transparent on the wire.
/// </summary>
module Ids =

    let private newUlid () = Ulid.NewUlid().ToString()

    /// <summary>Unique id for a single envelope (RFC §6.1.1 <c>id</c>).</summary>
    type MessageId = MessageId of string

    /// <summary>Logical session identifier (RFC §6.1.1 <c>session_id</c>, §9).</summary>
    type SessionId = SessionId of string

    /// <summary>Durable job identifier (RFC §10).</summary>
    type JobId = JobId of string

    /// <summary>Stream identifier (RFC §11).</summary>
    type StreamId = StreamId of string

    /// <summary>Subscription identifier (RFC §13).</summary>
    type SubscriptionId = SubscriptionId of string

    /// <summary>Artifact identifier (RFC §16).</summary>
    type ArtifactId = ArtifactId of string

    /// <summary>Permission lease identifier (RFC §15.5).</summary>
    type LeaseId = LeaseId of string

    /// <summary>Distributed trace id (RFC §17.1).</summary>
    type TraceId = TraceId of string

    /// <summary>Trace span id (RFC §17.1).</summary>
    type SpanId = SpanId of string

    /// <summary>Logical idempotency key (RFC §6.4).</summary>
    type IdempotencyKey = IdempotencyKey of string

    [<RequireQualifiedAccess>]
    module MessageId =
        let create () = MessageId(newUlid ())
        let value (MessageId v) = v
        let ofString (s: string) = MessageId s

    [<RequireQualifiedAccess>]
    module SessionId =
        let create () = SessionId(newUlid ())
        let value (SessionId v) = v
        let ofString (s: string) = SessionId s

    [<RequireQualifiedAccess>]
    module JobId =
        let create () = JobId(newUlid ())
        let value (JobId v) = v
        let ofString (s: string) = JobId s

    [<RequireQualifiedAccess>]
    module StreamId =
        let create () = StreamId(newUlid ())
        let value (StreamId v) = v
        let ofString (s: string) = StreamId s

    [<RequireQualifiedAccess>]
    module SubscriptionId =
        let create () = SubscriptionId(newUlid ())
        let value (SubscriptionId v) = v
        let ofString (s: string) = SubscriptionId s

    [<RequireQualifiedAccess>]
    module ArtifactId =
        let create () = ArtifactId(newUlid ())
        let value (ArtifactId v) = v
        let ofString (s: string) = ArtifactId s

    [<RequireQualifiedAccess>]
    module LeaseId =
        let create () = LeaseId(newUlid ())
        let value (LeaseId v) = v
        let ofString (s: string) = LeaseId s

    [<RequireQualifiedAccess>]
    module TraceId =
        let create () = TraceId(newUlid ())
        let value (TraceId v) = v
        let ofString (s: string) = TraceId s

    [<RequireQualifiedAccess>]
    module SpanId =
        let create () = SpanId(newUlid ())
        let value (SpanId v) = v
        let ofString (s: string) = SpanId s

    [<RequireQualifiedAccess>]
    module IdempotencyKey =
        let create (s: string) = IdempotencyKey s
        let value (IdempotencyKey v) = v
