module ARCP.UnitTests.EnvelopeBuilderTests

open Xunit
open FsUnit.Xunit
open ARCP.Core

let private base' () =
    Envelope.create "session.hello" (Json.parseElement "{}")

[<Fact>]
let ``withSessionId attaches the id`` () =
    let env = base' () |> Envelope.withSessionId (SessionId.ofString "sess_1")
    env.SessionId |> should equal (Some "sess_1")

[<Fact>]
let ``withJobId attaches the id`` () =
    let env = base' () |> Envelope.withJobId (JobId.ofString "job_42")
    env.JobId |> should equal (Some "job_42")

[<Fact>]
let ``withTraceId attaches the id`` () =
    let env = base' () |> Envelope.withTraceId (TraceId.ofString "abc")
    env.TraceId |> should equal (Some "abc")

[<Fact>]
let ``withEventSeq attaches the seq`` () =
    let env = base' () |> Envelope.withEventSeq 7L
    env.EventSeq |> should equal (Some 7L)

[<Fact>]
let ``withId overrides the message id`` () =
    let env = base' () |> Envelope.withId "msg-1"
    env.Id |> should equal "msg-1"

[<Fact>]
let ``create stamps the protocol version`` () =
    let env = base' ()
    env.Arcp |> should equal Version.Protocol
