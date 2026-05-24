module ARCP.UnitTests.EnvelopeOptTests

open Xunit
open FsUnit.Xunit
open ARCP.Core

let private mkEnv () =
    Envelope.create "session.hello" (Json.parseElement "{}")

[<Fact>]
let ``sessionIdOpt returns Some when present`` () =
    let env = mkEnv () |> Envelope.withSessionId (SessionId.ofString "sess_x")

    match Envelope.sessionIdOpt env with
    | Some sid -> sid.Value |> should equal "sess_x"
    | None -> failwith "expected Some"

[<Fact>]
let ``sessionIdOpt returns None when absent`` () =
    Envelope.sessionIdOpt (mkEnv ()) |> should equal None

[<Fact>]
let ``jobIdOpt returns Some when present`` () =
    let env = mkEnv () |> Envelope.withJobId (JobId.ofString "job_x")

    match Envelope.jobIdOpt env with
    | Some jid -> jid.Value |> should equal "job_x"
    | None -> failwith "expected Some"

[<Fact>]
let ``jobIdOpt returns None when absent`` () =
    Envelope.jobIdOpt (mkEnv ()) |> should equal None

[<Fact>]
let ``Glob isMatch handles ? wildcard for single char`` () =
    Glob.isMatch "a?c" "abc" |> should equal true
    Glob.isMatch "a?c" "ac" |> should equal false
    Glob.isMatch "a?c" "a/c" |> should equal false

[<Fact>]
let ``Glob isMatch exact string match short-circuits`` () =
    Glob.isMatch "exact" "exact" |> should equal true
    Glob.isMatch "exact" "other" |> should equal false

[<Fact>]
let ``ChunkAssembler IsClosed is initially false`` () =
    let asm = ARCP.Client.Internal.ChunkAssembler()
    asm.IsClosed |> should equal false
