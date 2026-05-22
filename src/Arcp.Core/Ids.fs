namespace ARCP.Core

open System
open Cysharp

/// Strongly-typed identifier wrappers. Each is a `[<Struct>]` over a
/// single string so the runtime cost is one register and there is no
/// allocation for parameter passing.
/// Wire identifier for a single envelope (`envelope.id`).
[<Struct>]
type MessageId =
    | MessageId of string

    /// The raw string form, as it appears on the wire.
    member this.Value = let (MessageId v) = this in v
    override this.ToString() = this.Value

/// Wire identifier for an ARCP session (`envelope.session_id`).
[<Struct>]
type SessionId =
    | SessionId of string

    /// The raw string form, as it appears on the wire.
    member this.Value = let (SessionId v) = this in v
    override this.ToString() = this.Value

/// Wire identifier for a submitted job (`envelope.job_id`).
[<Struct>]
type JobId =
    | JobId of string

    /// The raw string form, as it appears on the wire.
    member this.Value = let (JobId v) = this in v
    override this.ToString() = this.Value

/// Wire identifier for a streamed result (`result_chunk.result_id`).
[<Struct>]
type ResultId =
    | ResultId of string

    /// The raw string form, as it appears on the wire.
    member this.Value = let (ResultId v) = this in v
    override this.ToString() = this.Value

[<RequireQualifiedAccess>]
module MessageId =
    /// Mint a fresh `MessageId` from a ULID.
    let newId () : MessageId = MessageId(Ulid.NewUlid().ToString())

    /// Wrap an existing string. Throws on empty input; prefer
    /// `tryOfString` at API boundaries.
    let ofString (s: string) : MessageId =
        if System.String.IsNullOrEmpty s then
            invalidArg "s" "MessageId may not be empty"

        MessageId s

    /// Smart constructor returning `Result` for callers that prefer
    /// total functions.
    let tryOfString (s: string) : Result<MessageId, string> =
        if System.String.IsNullOrEmpty s then
            Error "MessageId may not be empty"
        else
            Ok(MessageId s)

[<RequireQualifiedAccess>]
module SessionId =
    /// Mint a fresh `SessionId` with the spec-canonical `sess_` prefix.
    let newId () : SessionId =
        SessionId("sess_" + Ulid.NewUlid().ToString())

    let ofString (s: string) : SessionId =
        if System.String.IsNullOrEmpty s then
            invalidArg "s" "SessionId may not be empty"

        SessionId s

    let tryOfString (s: string) : Result<SessionId, string> =
        if System.String.IsNullOrEmpty s then
            Error "SessionId may not be empty"
        else
            Ok(SessionId s)

[<RequireQualifiedAccess>]
module JobId =
    /// Mint a fresh `JobId` with the spec-canonical `job_` prefix.
    let newId () : JobId =
        JobId("job_" + Ulid.NewUlid().ToString())

    let ofString (s: string) : JobId =
        if System.String.IsNullOrEmpty s then
            invalidArg "s" "JobId may not be empty"

        JobId s

    let tryOfString (s: string) : Result<JobId, string> =
        if System.String.IsNullOrEmpty s then
            Error "JobId may not be empty"
        else
            Ok(JobId s)

[<RequireQualifiedAccess>]
module ResultId =
    /// Mint a fresh `ResultId` with the spec-canonical `res_` prefix.
    let newId () : ResultId =
        ResultId("res_" + Ulid.NewUlid().ToString())

    let ofString (s: string) : ResultId =
        if System.String.IsNullOrEmpty s then
            invalidArg "s" "ResultId may not be empty"

        ResultId s

    let tryOfString (s: string) : Result<ResultId, string> =
        if System.String.IsNullOrEmpty s then
            Error "ResultId may not be empty"
        else
            Ok(ResultId s)
