module ARCP.UnitTests.JobErrorMapperTests

open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Client.Internal

[<Theory>]
[<InlineData("PERMISSION_DENIED")>]
[<InlineData("LEASE_SUBSET_VIOLATION")>]
[<InlineData("JOB_NOT_FOUND")>]
[<InlineData("DUPLICATE_KEY")>]
[<InlineData("AGENT_NOT_AVAILABLE")>]
[<InlineData("AGENT_VERSION_NOT_AVAILABLE")>]
[<InlineData("CANCELLED")>]
[<InlineData("TIMEOUT")>]
[<InlineData("HEARTBEAT_LOST")>]
[<InlineData("LEASE_EXPIRED")>]
[<InlineData("BUDGET_EXHAUSTED")>]
[<InlineData("INVALID_REQUEST")>]
[<InlineData("UNAUTHENTICATED")>]
[<InlineData("RESUME_WINDOW_EXPIRED")>]
[<InlineData("INTERNAL_ERROR")>]
let ``ofWire round-trips canonical code`` (code: string) =
    let err = JobErrorMapper.ofWire code "msg" None "job_x"
    ARCPError.code err |> should equal code

[<Fact>]
let ``ofWire unknown code maps to Unknown preserving code`` () =
    // §90: unknown codes round-trip via the Unknown arm (not InternalError).
    let err = JobErrorMapper.ofWire "FUTURE_CODE" "msg" None ""

    match err with
    | ARCPError.Unknown("FUTURE_CODE", "msg", false) -> ()
    | other -> failwithf "expected Unknown, got %A" other

[<Fact>]
let ``ofWireWith honors wire retryable for unknown code`` () =
    match JobErrorMapper.ofWireWith "FUTURE_CODE" "msg" None false None with
    | ARCPError.Unknown(_, _, r) -> r |> should equal false
    | other -> failwithf "expected Unknown, got %A" other

[<Fact>]
let ``BUDGET_EXHAUSTED maps from upstream-style error`` () =
    let err =
        JobErrorMapper.ofWire "BUDGET_EXHAUSTED" "credit limit reached" None "job_x"

    match err with
    | ARCPError.BudgetExhausted _ -> ()
    | other -> failwithf "expected BudgetExhausted, got %A" other
