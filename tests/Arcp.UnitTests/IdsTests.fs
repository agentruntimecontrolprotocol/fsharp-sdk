module ARCP.UnitTests.IdsTests

open Xunit
open FsUnit.Xunit
open ARCP.Core

[<Fact>]
let ``MessageId.newId returns a non-empty value`` () =
    let id = MessageId.newId ()
    id.Value |> should not' (be NullOrEmptyString)

[<Fact>]
let ``SessionId.newId carries the sess_ prefix`` () =
    let id = SessionId.newId ()
    id.Value.StartsWith "sess_" |> should equal true

[<Fact>]
let ``JobId.newId carries the job_ prefix`` () =
    let id = JobId.newId ()
    id.Value.StartsWith "job_" |> should equal true

[<Fact>]
let ``ResultId.newId carries the res_ prefix`` () =
    let id = ResultId.newId ()
    id.Value.StartsWith "res_" |> should equal true

[<Fact>]
let ``tryOfString rejects empty strings`` () =
    match MessageId.tryOfString "" with
    | Error _ -> ()
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``tryOfString accepts non-empty strings`` () =
    match SessionId.tryOfString "abc" with
    | Ok id -> id.Value |> should equal "abc"
    | Error e -> failwithf "expected Ok, got %s" e

[<Fact>]
let ``ofString throws on empty input`` () =
    Assert.Throws<System.ArgumentException>(fun () -> JobId.ofString "" |> ignore)
    |> ignore

[<Fact>]
let ``MessageId Equals is structural`` () =
    let a = MessageId.ofString "abc"
    let b = MessageId.ofString "abc"
    a |> should equal b
