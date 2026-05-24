module ARCP.UnitTests.TraceTests

open Xunit
open FsUnit.Xunit
open ARCP.Core

[<Fact>]
let ``TraceId.newId returns 32 lowercase hex chars`` () =
    let id = TraceId.newId ()
    id.Value.Length |> should equal 32
    id.Value |> Seq.forall (fun c -> System.Char.IsDigit c || (c >= 'a' && c <= 'f')) |> should equal true

[<Fact>]
let ``TraceId.ofString round-trips`` () =
    let v = "abc"
    (TraceId.ofString v).Value |> should equal v

[<Fact>]
let ``TraceId ToString returns value`` () =
    let id = TraceId.ofString "deadbeef"
    id.ToString() |> should equal "deadbeef"

[<Fact>]
let ``SpanId.newId returns 16 lowercase hex chars`` () =
    let id = SpanId.newId ()
    id.Value.Length |> should equal 16
    id.Value |> Seq.forall (fun c -> System.Char.IsDigit c || (c >= 'a' && c <= 'f')) |> should equal true

[<Fact>]
let ``SpanId.ofString round-trips`` () =
    let v = "1234"
    (SpanId.ofString v).Value |> should equal v

[<Fact>]
let ``SpanId ToString returns value`` () =
    let id = SpanId.ofString "cafef00d"
    id.ToString() |> should equal "cafef00d"

[<Fact>]
let ``CredentialId.newId uses cred_ prefix`` () =
    let id = CredentialId.newId ()
    id.StartsWith "cred_" |> should equal true
    id.Length |> should be (greaterThan 5)

[<Fact>]
let ``CredentialId.newId returns unique values`` () =
    let a = CredentialId.newId ()
    let b = CredentialId.newId ()
    a |> should not' (equal b)

[<Fact>]
let ``Ids ToString round-trip via ofString`` () =
    let mid = MessageId.ofString "m1"
    mid.ToString() |> should equal "m1"
    let sid = SessionId.ofString "s1"
    sid.ToString() |> should equal "s1"
    let jid = JobId.ofString "j1"
    jid.ToString() |> should equal "j1"
    let rid = ResultId.ofString "r1"
    rid.ToString() |> should equal "r1"

[<Fact>]
let ``Id tryOfString accepts non-empty for all wrappers`` () =
    (JobId.tryOfString "ok").IsOk |> should equal true
    (ResultId.tryOfString "ok").IsOk |> should equal true
    (MessageId.tryOfString "ok").IsOk |> should equal true
    (SessionId.tryOfString "ok").IsOk |> should equal true

[<Fact>]
let ``Id tryOfString rejects null and empty`` () =
    (JobId.tryOfString null).IsError |> should equal true
    (ResultId.tryOfString "").IsError |> should equal true
    (MessageId.tryOfString null).IsError |> should equal true
    (SessionId.tryOfString "").IsError |> should equal true
