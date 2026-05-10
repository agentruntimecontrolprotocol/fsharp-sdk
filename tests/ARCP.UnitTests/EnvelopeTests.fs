module ARCP.UnitTests.EnvelopeTests

open System.Text.Json
open Xunit
open ARCP
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Session
open ARCP.Messages.Registry
open ARCP.Transport

[<Fact>]
let ``wire envelope exposes top-level type and payload`` () =
    let msg = SessionClose { Reason = Some "done" }

    let env = Envelopes.sessionClose { Reason = Some "done" }
    let s = Transport.serializeEnvelope env
    use doc = JsonDocument.Parse s
    Assert.Equal("session.close", doc.RootElement.GetProperty("type").GetString())
    let mutable payloadEl = Unchecked.defaultof<JsonElement>
    Assert.True(doc.RootElement.TryGetProperty("payload", &payloadEl))

[<Fact>]
let ``priority serializes as lowercase string`` () =
    let env = Envelopes.ack { Message = None } |> Envelope.withPriority Critical

    let s = Transport.serializeEnvelope env
    Assert.Contains("\"critical\"", s)

[<Fact>]
let ``envelope round-trips preserves metadata`` () =
    let env =
        Envelopes.ping { Nonce = Some "n1" }
        |> Envelope.withSession (SessionId "s1")
        |> Envelope.withPriority High

    let s = Transport.serializeEnvelope env

    match Transport.parseEnvelope s with
    | Ok decoded ->
        Assert.Equal(env.Type, decoded.Type)
        Assert.Equal(env.SessionId, decoded.SessionId)
        Assert.Equal(env.Priority, decoded.Priority)
    | Error e -> failwithf "parse failed: %A" e
