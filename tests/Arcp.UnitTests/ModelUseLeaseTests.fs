module ARCP.UnitTests.ModelUseLeaseTests

open System
open Xunit
open FsUnit.Xunit
open ARCP.Core

[<Fact>]
let ``validateLeaseOp allows model use match`` () =
    let lease = Lease.empty |> Lease.withCapability Capabilities.ModelUse [ "tier-fast/*" ]
    match Lease.validateLeaseOp lease None Map.empty DateTimeOffset.UtcNow Capabilities.ModelUse "tier-fast/gpt-4o-mini" with
    | Ok () -> ()
    | other -> failwithf "expected Ok, got %A" other

[<Fact>]
let ``validateLeaseOp denies model use miss`` () =
    let lease = Lease.empty |> Lease.withCapability Capabilities.ModelUse [ "tier-fast/*" ]
    match Lease.validateLeaseOp lease None Map.empty DateTimeOffset.UtcNow Capabilities.ModelUse "tier-pro/gpt-4o" with
    | Error (ARCPError.PermissionDenied _) -> ()
    | other -> failwithf "expected PermissionDenied, got %A" other

[<Fact>]
let ``isSubset rejects child model use expansion`` () =
    let parent = Lease.empty |> Lease.withCapability Capabilities.ModelUse [ "tier-fast/*" ]
    let child = Lease.empty |> Lease.withCapability Capabilities.ModelUse [ "tier-pro/*" ]
    match Lease.isSubset child parent Map.empty None None with
    | Error (ARCPError.LeaseSubsetViolation _) -> ()
    | other -> failwithf "expected LeaseSubsetViolation, got %A" other

[<Fact>]
let ``isSubset accepts child model use subset`` () =
    let parent =
        Lease.empty
        |> Lease.withCapability Capabilities.ModelUse [ "tier-fast/*"; "tier-pro/*" ]
    let child = Lease.empty |> Lease.withCapability Capabilities.ModelUse [ "tier-fast/*" ]
    match Lease.isSubset child parent Map.empty None None with
    | Ok () -> ()
    | other -> failwithf "expected Ok, got %A" other
