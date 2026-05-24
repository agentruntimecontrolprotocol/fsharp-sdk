module ARCP.UnitTests.CodecMessageRoundTripTests

open System
open Xunit
open FsUnit.Xunit
open ARCP.Core

let private rt (msg: Message) : Message =
    let env = Codec.toEnvelope msg
    let wire = Codec.writeEnvelope env

    match Codec.readEnvelope wire with
    | Ok env ->
        match Codec.toMessage env with
        | Ok m -> m
        | Error e -> failwithf "decode failed: %A" e
    | Error e -> failwithf "envelope parse failed: %A" e

let private now = DateTimeOffset.Parse "2026-01-01T00:00:00Z"

let private welcome =
    Message.SessionWelcome
        {
            Runtime = { Name = "rt"; Version = "1" }
            ResumeToken = "r"
            ResumeWindowSec = 600
            HeartbeatIntervalSec = Some 30
            Capabilities =
                {
                    Encodings = [ "json" ]
                    Features = Features.All
                    Agents = AgentInventory.Flat [ "echo" ]
                }
        }

let private listJobs = Message.SessionListJobs { Filter = None; Limit = None; Cursor = None }

let private jobs =
    Message.SessionJobs
        {
            RequestId = "r"
            Jobs = []
            NextCursor = None
        }

let private bye = Message.SessionBye { Reason = Some "x" }

let private sessionError =
    Message.SessionError
        {
            Code = "X"
            Message = "y"
            Retryable = false
            Details = None
        }

let private submit =
    Message.JobSubmit
        {
            Agent = "echo"
            Input = Json.serializeToElement<int> 0
            LeaseRequest = Some Lease.empty
            LeaseConstraints = None
            IdempotencyKey = None
            MaxRuntimeSec = None
        }

let private accepted =
    Message.JobAccepted
        {
            JobId = "j"
            Lease = Lease.empty
            LeaseConstraints = None
            Budget = None
            Credentials = None
            AcceptedAt = now
            TraceId = None
        }

let private resultMsg =
    Message.JobResult
        {
            FinalStatus = JobStatus.Success
            Result = Some(Json.serializeToElement "ok")
            ResultId = None
            ResultSize = None
            Summary = None
        }

let private errorMsg =
    Message.JobError
        {
            FinalStatus = JobStatus.Error
            Code = "X"
            Message = "y"
            Retryable = false
            Details = None
        }

let private cancel = Message.JobCancel { JobId = "j"; Reason = None }

let private subscribe =
    Message.JobSubscribe
        {
            JobId = "j"
            FromEventSeq = None
            History = None
        }

let private subscribed =
    Message.JobSubscribed
        {
            JobId = "j"
            CurrentStatus = JobStatus.Running
            Agent = "echo"
            Lease = Lease.empty
            ParentJobId = None
            TraceId = None
            SubscribedFrom = 0L
            Replayed = false
        }

let private unsubscribe = Message.JobUnsubscribe { JobId = "j" }

[<Theory>]
[<InlineData "welcome">]
[<InlineData "list_jobs">]
[<InlineData "jobs">]
[<InlineData "bye">]
[<InlineData "session_error">]
[<InlineData "submit">]
[<InlineData "accepted">]
[<InlineData "result">]
[<InlineData "error">]
[<InlineData "cancel">]
[<InlineData "subscribe">]
[<InlineData "subscribed">]
[<InlineData "unsubscribe">]
let ``every message type round-trips through Codec`` (which: string) =
    let original =
        match which with
        | "welcome" -> welcome
        | "list_jobs" -> listJobs
        | "jobs" -> jobs
        | "bye" -> bye
        | "session_error" -> sessionError
        | "submit" -> submit
        | "accepted" -> accepted
        | "result" -> resultMsg
        | "error" -> errorMsg
        | "cancel" -> cancel
        | "subscribe" -> subscribe
        | "subscribed" -> subscribed
        | "unsubscribe" -> unsubscribe
        | other -> failwithf "no case: %s" other

    let back = rt original
    Message.typeOf back |> should equal (Message.typeOf original)

[<Fact>]
let ``every job event body kind round-trips`` () =
    let el = Json.serializeToElement<int> 0

    let bodies =
        [
            JobEventBody.Log(LogLevel.Debug, "d")
            JobEventBody.Log(LogLevel.Info, "i")
            JobEventBody.Log(LogLevel.Warn, "w")
            JobEventBody.Log(LogLevel.Error, "e")
            JobEventBody.Thought "t"
            JobEventBody.ToolCall("name", el, "c")
            JobEventBody.ToolResult("c", ToolOutcome.Result el)
            JobEventBody.ToolResult("c", ToolOutcome.Error("X", "y", true))
            JobEventBody.Status("phase", Some "msg")
            JobEventBody.Metric("m", 1m, Some "u", Some(Map.ofList [ "k", "v" ]))
            JobEventBody.ArtifactRef("u", "ct", Some 1L, Some "h")
            JobEventBody.Progress(1m, Some 2m, Some "u", Some "m")
            JobEventBody.ResultChunk("r", 0L, "d", ChunkEncoding.Utf8, false)
            JobEventBody.ResultChunk("r", 1L, "AAA=", ChunkEncoding.Base64, true)
            JobEventBody.XVendor("x-vendor.foo", el)
        ]

    for b in bodies do
        let env =
            Message.JobEvent
                {
                    Kind = JobEventBody.kind b
                    Ts = now
                    Body = b
                }

        match rt env with
        | Message.JobEvent p -> JobEventBody.kind p.Body |> should equal (JobEventBody.kind b)
        | _ -> failwith "wrong case"
