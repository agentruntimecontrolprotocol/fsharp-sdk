namespace ARCP.Core

open System
open System.Text.Json

/// Canonical ARCP error taxonomy (spec §12). Fifteen DU cases —
/// every wire error code has exactly one DU arm.
///
/// Only `Timeout`, `HeartbeatLost`, and `InternalError` are retryable
/// per §12; the remaining twelve arms are not.
///
/// F# consumers prefer `Result<_, ARCPError>` for expected outcomes.
/// `ArcpException` (below) wraps the same value for C# callers and
/// for fatal paths where exceptions are the idiom.
[<RequireQualifiedAccess>]
type ARCPError =
    | PermissionDenied of message: string * details: JsonElement option
    | LeaseSubsetViolation of message: string * details: JsonElement option
    | JobNotFound of jobId: string
    | DuplicateKey of key: string
    | AgentNotAvailable of agent: string
    | AgentVersionNotAvailable of agent: string * version: string
    | Cancelled of reason: string option
    | Timeout of afterSec: int
    | ResumeWindowExpired of requestedSeq: int64 * windowSec: int
    | HeartbeatLost
    | LeaseExpired of expiredAt: DateTimeOffset
    | BudgetExhausted of currency: string
    | InvalidRequest of message: string * details: JsonElement option
    | Unauthenticated of message: string
    | InternalError of message: string
    /// Forward-compatible arm for wire error codes this SDK version
    /// does not model, carrying the wire `retryable` flag verbatim (§12).
    | Unknown of code: string * message: string * retryable: bool

[<RequireQualifiedAccess>]
module ARCPError =
    /// Wire-canonical error code (spec §12).
    let code (e: ARCPError) : string =
        match e with
        | ARCPError.PermissionDenied _ -> "PERMISSION_DENIED"
        | ARCPError.LeaseSubsetViolation _ -> "LEASE_SUBSET_VIOLATION"
        | ARCPError.JobNotFound _ -> "JOB_NOT_FOUND"
        | ARCPError.DuplicateKey _ -> "DUPLICATE_KEY"
        | ARCPError.AgentNotAvailable _ -> "AGENT_NOT_AVAILABLE"
        | ARCPError.AgentVersionNotAvailable _ -> "AGENT_VERSION_NOT_AVAILABLE"
        | ARCPError.Cancelled _ -> "CANCELLED"
        | ARCPError.Timeout _ -> "TIMEOUT"
        | ARCPError.ResumeWindowExpired _ -> "RESUME_WINDOW_EXPIRED"
        | ARCPError.HeartbeatLost -> "HEARTBEAT_LOST"
        | ARCPError.LeaseExpired _ -> "LEASE_EXPIRED"
        | ARCPError.BudgetExhausted _ -> "BUDGET_EXHAUSTED"
        | ARCPError.InvalidRequest _ -> "INVALID_REQUEST"
        | ARCPError.Unauthenticated _ -> "UNAUTHENTICATED"
        | ARCPError.InternalError _ -> "INTERNAL_ERROR"
        | ARCPError.Unknown(c, _, _) -> c

    /// Human-readable message; suitable for `error.message` on the wire.
    let message (e: ARCPError) : string =
        match e with
        | ARCPError.PermissionDenied(m, _) -> m
        | ARCPError.LeaseSubsetViolation(m, _) -> m
        | ARCPError.JobNotFound j -> sprintf "Job %s not found" j
        | ARCPError.DuplicateKey k -> sprintf "Idempotency key %s already in use" k
        | ARCPError.AgentNotAvailable a -> sprintf "Agent %s is not registered" a
        | ARCPError.AgentVersionNotAvailable(a, v) -> sprintf "Agent %s@%s is not registered" a v
        | ARCPError.Cancelled(Some r) -> sprintf "Cancelled: %s" r
        | ARCPError.Cancelled None -> "Cancelled"
        | ARCPError.Timeout s -> sprintf "Timed out after %d seconds" s
        | ARCPError.ResumeWindowExpired(seq, w) ->
            sprintf "Resume window of %ds elapsed; event_seq %d no longer buffered" w seq
        | ARCPError.HeartbeatLost -> "Heartbeat lost"
        | ARCPError.LeaseExpired t -> sprintf "Lease expired at %O" t
        | ARCPError.BudgetExhausted c -> sprintf "%s budget exhausted" c
        | ARCPError.InvalidRequest(m, _) -> m
        | ARCPError.Unauthenticated m -> m
        | ARCPError.InternalError m -> m
        | ARCPError.Unknown(_, m, _) -> m

    /// Spec §12: retryable iff a different attempt could succeed.
    let retryable (e: ARCPError) : bool =
        match e with
        | ARCPError.Timeout _
        | ARCPError.HeartbeatLost
        | ARCPError.InternalError _ -> true
        | ARCPError.Unknown(_, _, r) -> r
        | _ -> false

    let details (e: ARCPError) : JsonElement option =
        match e with
        | ARCPError.PermissionDenied(_, d)
        | ARCPError.LeaseSubsetViolation(_, d)
        | ARCPError.InvalidRequest(_, d) -> d
        | _ -> None

/// Convenience alias for `ARCPError`. The protocol uses "ARCP"
/// all-caps, so the spec-named type is `ARCPError`; `SdkError`
/// is the F#-conventional name for callers who prefer it.
type SdkError = ARCPError

/// Exception form of `ARCPError` for C# callers and for paths
/// where the spec-canonical surface is "throw with code".
type ArcpException(error: ARCPError, ?inner: exn) =
    inherit Exception(ARCPError.message error, defaultArg inner null)
    member _.Error = error
    member _.Code = ARCPError.code error
    member _.Retryable = ARCPError.retryable error

/// Helpers bridging `Result<_, ARCPError>` and the throwing C#-style
/// surface. Named `ArcpResult` (not `Result`) so it does not shadow
/// FSharp.Core's `Result` for consumers that `open ARCP.Core` (#118).
[<RequireQualifiedAccess>]
module ArcpResult =
    /// Throw `ArcpException` on `Error`, return the value on `Ok`.
    /// The seam between internal `Result<_, ARCPError>` and the
    /// public C#-friendly throwing API.
    let unwrapOrThrow (r: Result<'T, ARCPError>) : 'T =
        match r with
        | Ok v -> v
        | Error e -> raise (ArcpException e)
