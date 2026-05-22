module ARCP.UnitTests.CodecTests

open System.Text.Json
open Xunit
open FsUnit.Xunit
open ARCP.Core

let private rt (msg: Message) =
    let env = Codec.toEnvelope msg
    let wire = Codec.writeEnvelope env

    let envRt =
        match Codec.readEnvelope wire with
        | Ok p -> p
        | Error e -> failwithf "%A" e

    match Codec.toMessage envRt with
    | Ok m -> m
    | Error e -> failwithf "%A" e

[<Fact>]
let ``session.hello roundtrips`` () =
    let hello: SessionHelloPayload =
        {
            Client = { Name = "test"; Version = "test" } // client version, not protocol version
            Auth =
                {
                    Scheme = "bearer"
                    Token = Some "tok"
                }
            Capabilities =
                {
                    Encodings = [ "json" ]
                    Features = Features.All
                }
            Resume = None
        }

    match rt (Message.SessionHello hello) with
    | Message.SessionHello p ->
        p.Client.Name |> should equal "test"
        p.Capabilities.Features |> should equal Features.All
    | _ -> failwith "wrong case"

[<Fact>]
let ``job.event with progress body roundtrips`` () =
    let event: JobEventPayload =
        {
            Kind = "progress"
            Ts = System.DateTimeOffset.Parse("2026-01-01T00:00:00Z")
            Body = JobEventBody.Progress(42m, Some 100m, Some "files", Some "halfway")
        }

    match rt (Message.JobEvent event) with
    | Message.JobEvent e ->
        match e.Body with
        | JobEventBody.Progress(c, t, u, m) ->
            c |> should equal 42m
            t |> should equal (Some 100m)
            u |> should equal (Some "files")
            m |> should equal (Some "halfway")
        | _ -> failwith "wrong body"
    | _ -> failwith "wrong case"

[<Fact>]
let ``unknown message type returns InvalidRequest`` () =
    let env =
        {
            Arcp = "1.1"
            Id = "x"
            Type = "nonsense.type"
            SessionId = None
            TraceId = None
            JobId = None
            EventSeq = None
            Payload = Json.parseElement "{}"
        }

    match Codec.toMessage env with
    | Error(ARCPError.InvalidRequest _) -> ()
    | other -> failwithf "expected InvalidRequest, got %A" other

[<Fact>]
let ``Message.countsInEventSeq excludes ping pong ack`` () =
    let ping =
        Message.SessionPing
            {
                Nonce = "n"
                SentAt = System.DateTimeOffset.UtcNow
            }

    let pong =
        Message.SessionPong
            {
                PingNonce = "n"
                ReceivedAt = System.DateTimeOffset.UtcNow
            }

    let ack = Message.SessionAck { LastProcessedSeq = 1L }

    let evt =
        Message.JobEvent
            {
                Kind = "log"
                Ts = System.DateTimeOffset.UtcNow
                Body = JobEventBody.Log(LogLevel.Info, "x")
            }

    Message.countsInEventSeq ping |> should equal false
    Message.countsInEventSeq pong |> should equal false
    Message.countsInEventSeq ack |> should equal false
    Message.countsInEventSeq evt |> should equal true
