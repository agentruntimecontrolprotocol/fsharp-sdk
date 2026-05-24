module ARCP.UnitTests.ErrorsTests

open System
open Xunit
open FsUnit.Xunit
open ARCP.Core

[<Fact>]
let ``code returns spec-canonical strings for every case`` () =
    ARCPError.code (ARCPError.PermissionDenied("x", None))
    |> should equal "PERMISSION_DENIED"

    ARCPError.code (ARCPError.LeaseSubsetViolation("x", None))
    |> should equal "LEASE_SUBSET_VIOLATION"

    ARCPError.code (ARCPError.JobNotFound "j") |> should equal "JOB_NOT_FOUND"
    ARCPError.code (ARCPError.DuplicateKey "k") |> should equal "DUPLICATE_KEY"

    ARCPError.code (ARCPError.AgentNotAvailable "a")
    |> should equal "AGENT_NOT_AVAILABLE"

    ARCPError.code (ARCPError.AgentVersionNotAvailable("a", "v"))
    |> should equal "AGENT_VERSION_NOT_AVAILABLE"

    ARCPError.code (ARCPError.Cancelled None) |> should equal "CANCELLED"
    ARCPError.code (ARCPError.Timeout 1) |> should equal "TIMEOUT"

    ARCPError.code (ARCPError.ResumeWindowExpired(0L, 60))
    |> should equal "RESUME_WINDOW_EXPIRED"

    ARCPError.code ARCPError.HeartbeatLost |> should equal "HEARTBEAT_LOST"

    ARCPError.code (ARCPError.LeaseExpired DateTimeOffset.UtcNow)
    |> should equal "LEASE_EXPIRED"

    ARCPError.code (ARCPError.BudgetExhausted "USD")
    |> should equal "BUDGET_EXHAUSTED"

    ARCPError.code (ARCPError.InvalidRequest("x", None))
    |> should equal "INVALID_REQUEST"

    ARCPError.code (ARCPError.Unauthenticated "x") |> should equal "UNAUTHENTICATED"
    ARCPError.code (ARCPError.InternalError "x") |> should equal "INTERNAL_ERROR"

[<Fact>]
let ``retryable is true for Timeout, HeartbeatLost, InternalError only`` () =
    ARCPError.retryable (ARCPError.Timeout 1) |> should equal true
    ARCPError.retryable ARCPError.HeartbeatLost |> should equal true
    ARCPError.retryable (ARCPError.InternalError "x") |> should equal true

    ARCPError.retryable (ARCPError.LeaseExpired DateTimeOffset.UtcNow)
    |> should equal false

    ARCPError.retryable (ARCPError.BudgetExhausted "USD") |> should equal false
    ARCPError.retryable (ARCPError.JobNotFound "j") |> should equal false

[<Fact>]
let ``message formats human-readable strings`` () =
    ARCPError.message (ARCPError.JobNotFound "j-1")
    |> should equal "Job j-1 not found"

    ARCPError.message (ARCPError.Cancelled None) |> should equal "Cancelled"

    ARCPError.message (ARCPError.Cancelled(Some "user"))
    |> should equal "Cancelled: user"

    ARCPError.message (ARCPError.Timeout 5)
    |> should equal "Timed out after 5 seconds"

    ARCPError.message (ARCPError.BudgetExhausted "USD")
    |> should equal "USD budget exhausted"

    ARCPError.message ARCPError.HeartbeatLost |> should equal "Heartbeat lost"

    ARCPError.message (ARCPError.AgentNotAvailable "echo")
    |> should equal "Agent echo is not registered"

    ARCPError.message (ARCPError.AgentVersionNotAvailable("echo", "2"))
    |> should equal "Agent echo@2 is not registered"

    ARCPError.message (ARCPError.DuplicateKey "k")
    |> should equal "Idempotency key k already in use"

    ARCPError.message (ARCPError.ResumeWindowExpired(7L, 60))
    |> should equal "Resume window of 60s elapsed; event_seq 7 no longer buffered"

[<Fact>]
let ``details extracts payload only for cases that carry one`` () =
    let el = Json.nullElement ()

    (ARCPError.details (ARCPError.PermissionDenied("x", Some el))).IsSome
    |> should equal true

    (ARCPError.details (ARCPError.InvalidRequest("x", Some el))).IsSome
    |> should equal true

    (ARCPError.details (ARCPError.LeaseSubsetViolation("x", Some el))).IsSome
    |> should equal true

    (ARCPError.details (ARCPError.JobNotFound "j")).IsNone |> should equal true
    (ARCPError.details ARCPError.HeartbeatLost).IsNone |> should equal true

[<Fact>]
let ``ArcpException exposes Code and Retryable`` () =
    let err = ARCPError.Timeout 10
    let ex = ArcpException err
    ex.Code |> should equal "TIMEOUT"
    ex.Retryable |> should equal true
    ex.Error |> should equal err

[<Fact>]
let ``Result.unwrapOrThrow throws ArcpException for Error`` () =
    let err = ARCPError.JobNotFound "j"

    let ex =
        Assert.Throws<ArcpException>(fun () -> Result.unwrapOrThrow (Error err) |> ignore)

    ex.Error |> should equal err

[<Fact>]
let ``Result.unwrapOrThrow returns value for Ok`` () =
    Result.unwrapOrThrow (Ok 42) |> should equal 42
