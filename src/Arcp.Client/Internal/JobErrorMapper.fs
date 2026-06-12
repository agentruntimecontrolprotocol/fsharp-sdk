namespace ARCP.Client.Internal

open System
open System.Text.Json
open ARCP.Core

/// Map a wire `job.error` / `session.error` code string back to an
/// `ARCPError` DU, parsing structured fields out of the `details`
/// payload where the DU carries them. The reverse direction is
/// `ARCPError.code`.
[<RequireQualifiedAccess>]
module internal JobErrorMapper =
    let private prop (details: JsonElement option) (name: string) : JsonElement option =
        match details with
        | Some d when d.ValueKind = JsonValueKind.Object ->
            match d.TryGetProperty name with
            | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
            | _ -> None
        | _ -> None

    let private strField details name fallback =
        prop details name |> Option.map (fun v -> v.GetString()) |> Option.defaultValue fallback

    let private intField details name fallback =
        prop details name
        |> Option.bind (fun v ->
            match v.TryGetInt32() with
            | true, n -> Some n
            | _ -> None)
        |> Option.defaultValue fallback

    let private int64Field details name fallback =
        prop details name
        |> Option.bind (fun v ->
            match v.TryGetInt64() with
            | true, n -> Some n
            | _ -> None)
        |> Option.defaultValue fallback

    let private dateField details name fallback =
        prop details name
        |> Option.bind (fun v ->
            match v.TryGetDateTimeOffset() with
            | true, d -> Some d
            | _ -> None)
        |> Option.defaultValue fallback

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
        | "AGENT_VERSION_NOT_AVAILABLE" -> ARCPError.AgentVersionNotAvailable(message, strField details "version" "")
        | "CANCELLED" -> ARCPError.Cancelled(Some message)
        | "TIMEOUT" -> ARCPError.Timeout(intField details "timeout_sec" 0)
        | "HEARTBEAT_LOST" -> ARCPError.HeartbeatLost
        | "LEASE_EXPIRED" -> ARCPError.LeaseExpired(dateField details "expires_at" DateTimeOffset.MinValue)
        | "BUDGET_EXHAUSTED" -> ARCPError.BudgetExhausted(strField details "currency" "USD")
        | "INVALID_REQUEST" -> ARCPError.InvalidRequest(message, details)
        | "UNAUTHENTICATED" -> ARCPError.Unauthenticated message
        | "RESUME_WINDOW_EXPIRED" ->
            ARCPError.ResumeWindowExpired(int64Field details "from_seq" 0L, intField details "window_sec" 0)
        | _ -> ARCPError.InternalError message
