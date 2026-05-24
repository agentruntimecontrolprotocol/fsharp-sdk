module ARCP.UnitTests.AuthTests

open System.Collections.Generic
open System.Threading
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime.Auth

[<Fact>]
let ``StaticBearerVerifier resolves known tokens to a principal`` () =
    let tokens = readOnlyDict [ "tok-a", "alice"; "tok-b", "bob" ]
    let v = StaticBearerVerifier(tokens) :> IBearerVerifier

    match (v.VerifyAsync("tok-a", CancellationToken.None)).Result with
    | Ok p -> p.Id |> should equal "alice"
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``StaticBearerVerifier rejects unknown tokens`` () =
    let tokens = readOnlyDict [ "tok-a", "alice" ]
    let v = StaticBearerVerifier(tokens) :> IBearerVerifier

    match (v.VerifyAsync("bogus", CancellationToken.None)).Result with
    | Error(ARCPError.Unauthenticated _) -> ()
    | other -> failwithf "expected Unauthenticated, got %A" other

[<Fact>]
let ``DevModeBearerVerifier rejects empty tokens`` () =
    let v = DevModeBearerVerifier() :> IBearerVerifier

    match (v.VerifyAsync("", CancellationToken.None)).Result with
    | Error(ARCPError.Unauthenticated _) -> ()
    | other -> failwithf "expected Unauthenticated, got %A" other

[<Fact>]
let ``DevModeBearerVerifier prefixes principal id with dev:`` () =
    let v = DevModeBearerVerifier() :> IBearerVerifier

    match (v.VerifyAsync("anything", CancellationToken.None)).Result with
    | Ok p -> p.Id |> should equal "dev:anything"
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``AlwaysDenyVerifier always returns Unauthenticated`` () =
    let v = AlwaysDenyVerifier() :> IBearerVerifier

    match (v.VerifyAsync("whatever", CancellationToken.None)).Result with
    | Error(ARCPError.Unauthenticated _) -> ()
    | other -> failwithf "expected Unauthenticated, got %A" other

[<Fact>]
let ``AnonymousPrincipal id is anonymous`` () =
    let p = AnonymousPrincipal() :> IPrincipal
    p.Id |> should equal "anonymous"
    p.Labels.IsEmpty |> should equal true

[<Fact>]
let ``StringPrincipal with labels exposes them`` () =
    let labels = Map.ofList [ "team", "platform" ]
    let p = StringPrincipal("u-1", labels) :> IPrincipal
    p.Id |> should equal "u-1"
    p.Labels |> should equal labels

[<Fact>]
let ``StringPrincipal default labels are empty`` () =
    let p = StringPrincipal("u-2") :> IPrincipal
    p.Labels.IsEmpty |> should equal true
