module ARCP.UnitTests.MessagesTests

open System
open System.Text.Json
open Xunit
open ARCP
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Session
open ARCP.Messages.Control
open ARCP.Messages.Execution
open ARCP.Messages.Streaming
open ARCP.Messages.Human
open ARCP.Messages.Permissions
open ARCP.Messages.Subscriptions
open ARCP.Messages.Artifacts
open ARCP.Messages.Telemetry
open ARCP.Messages.Registry
open ARCP.Transport

let private jsonNull = JsonDocument.Parse("null").RootElement
let private jsonObj = JsonDocument.Parse("{\"k\":1}").RootElement

let private roundtrip (msg: MessageType) : MessageType =
    let env = Envelope.create (wireType msg) msg

    let s = Transport.serializeEnvelope env

    match Transport.parseEnvelope s with
    | Ok decoded -> decoded.Payload
    | Error e -> failwithf "parse failed: %A" e

[<Fact>]
let ``session.open round-trips`` () =
    let msg =
        SessionOpen
            {
                Arcp = Version.Protocol
                Client =
                    {
                        Kind = "test"
                        Version = "1"
                        Fingerprint = None
                        Principal = None
                    }
                Auth =
                    {
                        Scheme = "bearer"
                        Token = Some "abc"
                        Fingerprint = None
                    }
                Capabilities = Capabilities.empty
            }

    let decoded = roundtrip msg
    Assert.Equal(wireType msg, wireType decoded)

[<Fact>]
let ``session.accepted round-trips`` () =
    let msg =
        SessionAccepted
            {
                SessionId = SessionId "s1"
                Runtime =
                    {
                        Kind = "rt"
                        Version = "1"
                        Fingerprint = None
                        TrustLevel = None
                    }
                Capabilities = Capabilities.empty
                Lease = None
            }

    Assert.Equal("session.accepted", wireType (roundtrip msg))

[<Fact>]
let ``ping pong round-trip`` () =
    Assert.Equal("ping", wireType (roundtrip (Ping { Nonce = Some "x" })))
    Assert.Equal("pong", wireType (roundtrip (Pong { Nonce = None })))

[<Fact>]
let ``nack round-trips with details`` () =
    let msg =
        Nack
            {
                Code = "UNIMPLEMENTED"
                Message = "nope"
                Details = Some jsonObj
            }

    Assert.Equal("nack", wireType (roundtrip msg))

[<Fact>]
let ``tool.invoke and tool.result round-trip`` () =
    Assert.Equal("tool.invoke", wireType (roundtrip (ToolInvoke { Tool = "t"; Arguments = jsonObj })))

    Assert.Equal(
        "tool.result",
        wireType (
            roundtrip (
                ToolResult
                    {
                        Value = Some jsonObj
                        ResultRef = None
                    }
            )
        )
    )

[<Fact>]
let ``job.progress and job.completed round-trip`` () =
    Assert.Equal(
        "job.progress",
        wireType (
            roundtrip (
                JobProgress
                    {
                        Percent = Some 50
                        Message = Some "halfway"
                    }
            )
        )
    )

    Assert.Equal("job.completed", wireType (roundtrip (JobCompleted { Value = None; ResultRef = None })))

[<Fact>]
let ``stream.open chunk close round-trip`` () =
    Assert.Equal(
        "stream.open",
        wireType (
            roundtrip (
                StreamOpen
                    {
                        Kind = StreamKind.Text
                        ContentType = Some "text/plain"
                        Encoding = None
                    }
            )
        )
    )

    Assert.Equal(
        "stream.chunk",
        wireType (
            roundtrip (
                StreamChunk
                    {
                        Sequence = 0
                        Data = jsonObj
                        Sha256 = None
                    }
            )
        )
    )

    Assert.Equal("stream.close", wireType (roundtrip (StreamClose { Reason = None })))

[<Fact>]
let ``human.input round-trip`` () =
    let req =
        HumanInputRequest
            {
                Prompt = "?"
                ResponseSchema = None
                Default = None
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes 1.0
            }

    Assert.Equal("human.input.request", wireType (roundtrip req))

[<Fact>]
let ``permission.request and lease.granted round-trip`` () =
    let req =
        PermissionRequest
            {
                Permission = "fs.read"
                Resource = "/x"
                Operation = "read"
                Reason = None
                RequestedLeaseSeconds = Some 60
            }

    Assert.Equal("permission.request", wireType (roundtrip req))

    let lg =
        LeaseGranted
            {
                LeaseId = LeaseId "l1"
                Permission = "fs.read"
                Resource = "/x"
                Operation = "read"
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes 1.0
            }

    Assert.Equal("lease.granted", wireType (roundtrip lg))

[<Fact>]
let ``subscribe round-trip`` () =
    let sub =
        Subscribe
            {
                Filter =
                    {
                        SessionId = None
                        TraceId = None
                        JobId = None
                        StreamId = None
                        Types = Some [ "job.progress" ]
                        MinPriority = None
                    }
                Since = None
            }

    Assert.Equal("subscribe", wireType (roundtrip sub))

[<Fact>]
let ``artifact.put round-trip`` () =
    let p =
        ArtifactPut
            {
                MediaType = "text/plain"
                Data = "aGVsbG8="
                Sha256 = None
            }

    Assert.Equal("artifact.put", wireType (roundtrip p))

[<Fact>]
let ``log and metric round-trip`` () =
    let l =
        LogEntry
            {
                Level = Log.Info
                Message = "hi"
                Attributes = None
            }

    Assert.Equal("log", wireType (roundtrip l))

    let m =
        MetricSample
            {
                Name = Metric.TokensUsed
                Value = 1.0
                Unit = "tokens"
                Dims = None
            }

    Assert.Equal("metric", wireType (roundtrip m))
