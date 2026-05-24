module ARCP.UnitTests.LeaseTests

open System
open Xunit
open FsUnit.Xunit
open ARCP.Core

[<Fact>]
let ``glob ** matches any subpath`` () =
    Glob.isMatch "/workspace/**" "/workspace/src/main.fs" |> should equal true
    Glob.isMatch "/workspace/**" "/workspace/" |> should equal true
    Glob.isMatch "/data/*.txt" "/data/foo.txt" |> should equal true
    Glob.isMatch "/data/*.txt" "/data/sub/foo.txt" |> should equal false

[<Fact>]
let ``validateLeaseOp denies missing capability`` () =
    let lease = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/a/**" ]
    let now = DateTimeOffset.UtcNow

    match Lease.validateLeaseOp lease None Map.empty now Capabilities.FsWrite "/a/file" with
    | Error(ARCPError.PermissionDenied _) -> ()
    | other -> failwithf "expected PermissionDenied, got %A" other

[<Fact>]
let ``validateLeaseOp allows in-scope op`` () =
    let lease = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/a/**" ]
    let now = DateTimeOffset.UtcNow

    match Lease.validateLeaseOp lease None Map.empty now Capabilities.FsRead "/a/file" with
    | Ok() -> ()
    | other -> failwithf "expected Ok, got %A" other

[<Fact>]
let ``validateLeaseOp rejects after expires_at`` () =
    let lease = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/a/**" ]
    let now = DateTimeOffset.UtcNow
    let constraints = Some { ExpiresAt = now.AddSeconds(-1.0) }

    match Lease.validateLeaseOp lease constraints Map.empty now Capabilities.FsRead "/a/x" with
    | Error(ARCPError.LeaseExpired _) -> ()
    | other -> failwithf "expected LeaseExpired, got %A" other

[<Fact>]
let ``budget counters trigger BudgetExhausted`` () =
    let lease = Lease.empty |> Lease.withCapability Capabilities.ToolCall [ "search.*" ]
    let now = DateTimeOffset.UtcNow
    let budgets = Map.ofList [ "USD", 0m ]

    match Lease.validateLeaseOp lease None budgets now Capabilities.ToolCall "search.web" with
    | Error(ARCPError.BudgetExhausted c) -> c |> should equal "USD"
    | other -> failwithf "expected BudgetExhausted, got %A" other

[<Fact>]
let ``parseBudgetAmount accepts USD:5.00`` () =
    match Lease.parseBudgetAmount "USD:5.00" with
    | Ok("USD", v) -> v |> should equal 5.00m
    | other -> failwithf "expected Ok, got %A" other

[<Fact>]
let ``parseBudgetAmount rejects malformed`` () =
    match Lease.parseBudgetAmount "USD" with
    | Error _ -> ()
    | Ok _ -> failwith "expected error"

[<Fact>]
let ``isSubset rejects expanded namespace`` () =
    let parent = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/a/**" ]
    let child = Lease.empty |> Lease.withCapability Capabilities.FsWrite [ "/a/**" ]

    match Lease.isSubset child parent Map.empty None None with
    | Error(ARCPError.LeaseSubsetViolation _) -> ()
    | other -> failwithf "expected LeaseSubsetViolation, got %A" other

[<Fact>]
let ``isSubset accepts narrower child`` () =
    let parent = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/a/**" ]
    let child = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/a/sub/**" ]

    match Lease.isSubset child parent Map.empty None None with
    | Ok() -> ()
    | other -> failwithf "expected Ok, got %A" other

[<Fact>]
let ``isSubset accepts exact child`` () =
    let parent = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/a/**" ]
    let child = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/a/**" ]

    match Lease.isSubset child parent Map.empty None None with
    | Ok() -> ()
    | other -> failwithf "expected Ok, got %A" other

[<Fact>]
let ``isSubset accepts narrower s3 artifacts child`` () =
    let parent =
        Lease.empty |> Lease.withCapability Capabilities.NetFetch [ "s3://artifacts/**" ]

    let child =
        Lease.empty
        |> Lease.withCapability Capabilities.NetFetch [ "s3://artifacts/2026/**" ]

    match Lease.isSubset child parent Map.empty None None with
    | Ok() -> ()
    | other -> failwithf "expected Ok, got %A" other

[<Fact>]
let ``isSubset rejects broader s3 child`` () =
    let parent =
        Lease.empty |> Lease.withCapability Capabilities.NetFetch [ "s3://artifacts/**" ]

    let child = Lease.empty |> Lease.withCapability Capabilities.NetFetch [ "s3://**" ]

    match Lease.isSubset child parent Map.empty None None with
    | Error(ARCPError.LeaseSubsetViolation _) -> ()
    | other -> failwithf "expected LeaseSubsetViolation, got %A" other

[<Fact>]
let ``isSubset accepts literal child under single-star parent`` () =
    let parent = Lease.empty |> Lease.withCapability Capabilities.ToolCall [ "render.*" ]
    let child = Lease.empty |> Lease.withCapability Capabilities.ToolCall [ "render.png" ]

    match Lease.isSubset child parent Map.empty None None with
    | Ok() -> ()
    | other -> failwithf "expected Ok, got %A" other

[<Fact>]
let ``isSubset rejects literal child outside single-star parent`` () =
    let parent = Lease.empty |> Lease.withCapability Capabilities.ToolCall [ "render.*" ]
    let child = Lease.empty |> Lease.withCapability Capabilities.ToolCall [ "search.web" ]

    match Lease.isSubset child parent Map.empty None None with
    | Error(ARCPError.LeaseSubsetViolation _) -> ()
    | other -> failwithf "expected LeaseSubsetViolation, got %A" other

[<Fact>]
let ``isSubset rejects child expires_at past parent`` () =
    let parent = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/a/**" ]
    let child = parent
    let parentExpiry = DateTimeOffset.UtcNow.AddHours 1.0
    let childExpiry = parentExpiry.AddHours 1.0

    match Lease.isSubset child parent Map.empty (Some parentExpiry) (Some childExpiry) with
    | Error(ARCPError.LeaseSubsetViolation _) -> ()
    | other -> failwithf "expected LeaseSubsetViolation, got %A" other

[<Fact>]
let ``isSubset rejects child cost.budget over parent remaining`` () =
    let parent =
        Lease.empty |> Lease.withCapability Capabilities.CostBudget [ "USD:5.00" ]

    let child =
        Lease.empty |> Lease.withCapability Capabilities.CostBudget [ "USD:3.00" ]

    let parentRemaining = Map.ofList [ "USD", 2m ]

    match Lease.isSubset child parent parentRemaining None None with
    | Error(ARCPError.LeaseSubsetViolation _) -> ()
    | other -> failwithf "expected LeaseSubsetViolation, got %A" other
