module ARCP.UnitTests.EnvelopeTests

open System.Text.Json
open Xunit
open FsUnit.Xunit
open ARCP.Core

[<Fact>]
let ``envelope roundtrip preserves wire fields`` () =
    let payload = Json.parseElement """{"hello":"world"}"""
    let env =
        Envelope.create "session.hello" payload
        |> Envelope.withSessionId (SessionId.ofString "sess_1")
        |> Envelope.withTraceId (TraceId.ofString "trace1")
        |> Envelope.withJobId (JobId.ofString "job_42")
        |> Envelope.withEventSeq 7L
    let wire = Codec.writeEnvelope env
    let parsed =
        match Codec.readEnvelope wire with
        | Ok p -> p
        | Error e -> failwithf "%A" e
    parsed.Arcp |> should equal Version.Protocol
    parsed.Type |> should equal "session.hello"
    parsed.SessionId |> should equal (Some "sess_1")
    parsed.JobId |> should equal (Some "job_42")
    parsed.EventSeq |> should equal (Some 7L)

[<Fact>]
let ``envelope arcp field is "1.1"`` () =
    let env = Envelope.create "session.hello" (Json.parseElement "{}")
    let wire = Codec.writeEnvelope env
    wire |> should haveSubstring "\"arcp\":\"1.1\""
    wire |> should not' (haveSubstring "\"arcp\":\"1.0\"")
