module ARCP.UnitTests.FeatureNegotiationTests

open Xunit
open FsUnit.Xunit
open FsCheck
open FsCheck.Xunit
open ARCP.Core

[<Fact>]
let ``intersect of disjoint sets is empty`` () =
    let result = Features.intersect (Set.singleton "heartbeat") (Set.singleton "ack")
    result.IsEmpty |> should equal true

[<Fact>]
let ``intersect of subset returns subset`` () =
    let client = Set.ofList [ "heartbeat"; "ack" ]
    let runtime = Features.All
    let result = Features.intersect client runtime
    (result = client) |> should equal true

[<Fact>]
let ``negotiation includes provisioned credentials when both sides advertise`` () =
    let features = Set.ofList [ Features.ModelUse; Features.ProvisionedCredentials ]
    Features.intersect features features |> should equal features

[<Fact>]
let ``negotiation excludes provisioned credentials when only client advertises`` () =
    let client = Set.ofList [ Features.ModelUse; Features.ProvisionedCredentials ]
    let runtime = Set.empty
    Features.intersect client runtime |> should equal (Set.empty<string>)

[<Property>]
let ``intersect is commutative`` (a: string Set) (b: string Set) =
    Features.intersect a b = Features.intersect b a

[<Property>]
let ``intersect is idempotent`` (a: string Set) =
    Features.intersect a a = a

[<Property>]
let ``intersect is a subset of both inputs`` (a: string Set) (b: string Set) =
    let result = Features.intersect a b
    Set.isSubset result a && Set.isSubset result b
