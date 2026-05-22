namespace ARCP.Client.Internal

open System
open ARCP.Core

/// Map a wire `job.error` / `session.error` code string back to an
/// `ARCPError` DU. The reverse direction is `ARCPError.code`. Out
/// of scope: details payloads beyond `message`.
[<RequireQualifiedAccess>]
module internal JobErrorMapper =
    let ofWire
        (code: string)
        (message: string)
        (details: System.Text.Json.JsonElement option)
        (jobId: string)
        : ARCPError =
        match code with
        | "PERMISSION_DENIED" -> ARCPError.PermissionDenied(message, details)
        | "LEASE_SUBSET_VIOLATION" -> ARCPError.LeaseSubsetViolation(message, details)
        | "JOB_NOT_FOUND" -> ARCPError.JobNotFound jobId
        | "DUPLICATE_KEY" -> ARCPError.DuplicateKey message
        | "AGENT_NOT_AVAILABLE" -> ARCPError.AgentNotAvailable message
        | "AGENT_VERSION_NOT_AVAILABLE" -> ARCPError.AgentVersionNotAvailable(message, "")
        | "CANCELLED" -> ARCPError.Cancelled(Some message)
        | "TIMEOUT" -> ARCPError.Timeout 0
        | "HEARTBEAT_LOST" -> ARCPError.HeartbeatLost
        | "LEASE_EXPIRED" -> ARCPError.LeaseExpired DateTimeOffset.MinValue
        | "BUDGET_EXHAUSTED" -> ARCPError.BudgetExhausted "USD"
        | "INVALID_REQUEST" -> ARCPError.InvalidRequest(message, details)
        | "UNAUTHENTICATED" -> ARCPError.Unauthenticated message
        | "RESUME_WINDOW_EXPIRED" -> ARCPError.ResumeWindowExpired(0L, 0)
        | _ -> ARCPError.InternalError message
