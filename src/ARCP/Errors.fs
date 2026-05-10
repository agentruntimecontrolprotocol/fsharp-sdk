namespace ARCP

open System
open ARCP.Ids

/// <summary>
/// Canonical ARCP error taxonomy (RFC §18). Public APIs return
/// <c>Result&lt;'T, ARCPError&gt;</c> for fallible protocol-level operations;
/// exceptions are reserved for catastrophic situations (transport tear-down,
/// programmer error).
/// </summary>
module Errors =

    /// <summary>One case per RFC §18.2 canonical code.</summary>
    type ARCPError =
        | Cancelled of message: string
        | Unknown of message: string
        | InvalidArgument of field: string * detail: string
        | DeadlineExceeded of operation: string
        | NotFound of entity: string
        | AlreadyExists of entity: string
        | PermissionDenied of permission: string * resource: string
        | ResourceExhausted of detail: string * retryAfter: TimeSpan option
        | FailedPrecondition of detail: string
        | Aborted of detail: string
        | OutOfRange of field: string * detail: string
        | Unimplemented of section: string
        | Internal of cause: string * inner: exn option
        | Unavailable of detail: string
        | DataLoss of detail: string
        | Unauthenticated of message: string
        | HeartbeatLost of jobId: JobId * missed: int
        | LeaseExpired of leaseId: LeaseId * expiredAt: DateTimeOffset
        | LeaseRevoked of leaseId: LeaseId * reason: string
        | BackpressureOverflow of subjectId: string

    [<RequireQualifiedAccess>]
    module ARCPError =

        /// <summary>The canonical wire <c>code</c> string (RFC §18.2).</summary>
        let code =
            function
            | Cancelled _ -> "CANCELLED"
            | Unknown _ -> "UNKNOWN"
            | InvalidArgument _ -> "INVALID_ARGUMENT"
            | DeadlineExceeded _ -> "DEADLINE_EXCEEDED"
            | NotFound _ -> "NOT_FOUND"
            | AlreadyExists _ -> "ALREADY_EXISTS"
            | PermissionDenied _ -> "PERMISSION_DENIED"
            | ResourceExhausted _ -> "RESOURCE_EXHAUSTED"
            | FailedPrecondition _ -> "FAILED_PRECONDITION"
            | Aborted _ -> "ABORTED"
            | OutOfRange _ -> "OUT_OF_RANGE"
            | Unimplemented _ -> "UNIMPLEMENTED"
            | Internal _ -> "INTERNAL"
            | Unavailable _ -> "UNAVAILABLE"
            | DataLoss _ -> "DATA_LOSS"
            | Unauthenticated _ -> "UNAUTHENTICATED"
            | HeartbeatLost _ -> "HEARTBEAT_LOST"
            | LeaseExpired _ -> "LEASE_EXPIRED"
            | LeaseRevoked _ -> "LEASE_REVOKED"
            | BackpressureOverflow _ -> "BACKPRESSURE_OVERFLOW"

        /// <summary>Default retryability (RFC §18.3).</summary>
        let retryable =
            function
            | ResourceExhausted _
            | Unavailable _
            | DeadlineExceeded _
            | Internal _
            | Aborted _ -> true
            | _ -> false

        /// <summary>A human-readable summary suitable for logging.</summary>
        let message =
            function
            | Cancelled m -> m
            | Unknown m -> m
            | InvalidArgument(field, detail) -> sprintf "invalid argument %s: %s" field detail
            | DeadlineExceeded op -> sprintf "deadline exceeded: %s" op
            | NotFound e -> sprintf "not found: %s" e
            | AlreadyExists e -> sprintf "already exists: %s" e
            | PermissionDenied(p, r) -> sprintf "permission denied: %s on %s" p r
            | ResourceExhausted(detail, _) -> sprintf "resource exhausted: %s" detail
            | FailedPrecondition d -> sprintf "failed precondition: %s" d
            | Aborted d -> sprintf "aborted: %s" d
            | OutOfRange(f, d) -> sprintf "out of range %s: %s" f d
            | Unimplemented section -> sprintf "unimplemented (%s)" section
            | Internal(cause, _) -> sprintf "internal error: %s" cause
            | Unavailable d -> sprintf "unavailable: %s" d
            | DataLoss d -> sprintf "data loss: %s" d
            | Unauthenticated m -> m
            | HeartbeatLost(JobId j, n) -> sprintf "heartbeat lost on %s after %d missed deadlines" j n
            | LeaseExpired(LeaseId l, at) -> sprintf "lease %s expired at %O" l at
            | LeaseRevoked(LeaseId l, reason) -> sprintf "lease %s revoked: %s" l reason
            | BackpressureOverflow s -> sprintf "backpressure overflow on %s" s

        /// <summary>
        /// Suggested retry hint for <c>ResourceExhausted</c>; <c>None</c>
        /// otherwise.
        /// </summary>
        let retryAfter =
            function
            | ResourceExhausted(_, after) -> after
            | _ -> None
