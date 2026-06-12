module ARCP.UnitTests.WireFormatGoldenTests

open System.Text.Json
open Xunit
open FsUnit.Xunit
open ARCP.Core

/// Golden wire-format tests pinning the spec JSON shapes (§6.2,
/// §7.3, §8.2, §8.4). These guard cross-SDK interop: any non-F#
/// peer must be able to read/write these exact shapes.

let private ts = System.DateTimeOffset.Parse("2026-05-13T19:42:13Z")

/// Serialize a payload to its JSON object string.
let private payloadJson (v: 'T) = Json.serialize v

[<Fact>]
let ``log event body serializes flat with lowercase level`` () =
    let payload: JobEventPayload =
        {
            Kind = "log"
            Ts = ts
            Body = JobEventBody.Log(LogLevel.Info, "hello")
        }

    let json = payloadJson payload
    json |> should haveSubstring "\"body\":{\"level\":\"info\",\"message\":\"hello\"}"
    json |> should not' (haveSubstring "Log")
    json |> should haveSubstring "\"kind\":\"log\""

[<Fact>]
let ``progress event body matches spec example shape`` () =
    let payload: JobEventPayload =
        {
            Kind = "progress"
            Ts = ts
            Body = JobEventBody.Progress(47m, Some 120m, Some "files", Some "Refactoring")
        }

    let json = payloadJson payload
    json |> should haveSubstring "\"body\":{\"current\":47,\"total\":120,\"units\":\"files\",\"message\":\"Refactoring\"}"

[<Fact>]
let ``result_chunk body uses lowercase encoding and snake_case fields`` () =
    let payload: JobEventPayload =
        {
            Kind = "result_chunk"
            Ts = ts
            Body = JobEventBody.ResultChunk("res_1", 0L, "abc", ChunkEncoding.Utf8, true)
        }

    let json = payloadJson payload

    json
    |> should haveSubstring "\"body\":{\"result_id\":\"res_1\",\"chunk_seq\":0,\"data\":\"abc\",\"encoding\":\"utf8\",\"more\":true}"

[<Fact>]
let ``job.result final_status is lowercase`` () =
    let payload: JobResultPayload =
        {
            FinalStatus = JobStatus.Success
            Result = None
            ResultId = None
            ResultSize = None
            Summary = None
        }

    payloadJson payload |> should haveSubstring "\"final_status\":\"success\""

[<Fact>]
let ``tool_result with error serializes nested error object`` () =
    let payload: JobEventPayload =
        {
            Kind = "tool_result"
            Ts = ts
            Body = JobEventBody.ToolResult("call_1", ToolOutcome.Error("INTERNAL_ERROR", "boom", true))
        }

    let json = payloadJson payload
    json |> should haveSubstring "\"call_id\":\"call_1\""
    json |> should haveSubstring "\"error\":{\"code\":\"INTERNAL_ERROR\",\"message\":\"boom\",\"retryable\":true}"

[<Fact>]
let ``welcome capabilities agents serialize as plain array (rich)`` () =
    let payload: SessionWelcomePayload =
        {
            Runtime = { Name = "rt"; Version = "1.1.0" }
            ResumeToken = "rt_1"
            ResumeWindowSec = 600
            HeartbeatIntervalSec = Some 30
            Capabilities =
                {
                    Encodings = [ "json" ]
                    Features = set [ "heartbeat" ]
                    Agents =
                        AgentInventory.Rich
                            [ { Name = "code-refactor"; Versions = [ "1.0.0"; "2.0.0" ]; Default = Some "2.0.0" } ]
                }
        }

    let json = payloadJson payload
    json |> should haveSubstring "\"agents\":[{\"name\":\"code-refactor\",\"versions\":[\"1.0.0\",\"2.0.0\"],\"default\":\"2.0.0\"}]"

[<Fact>]
let ``welcome capabilities agents serialize as plain array (flat)`` () =
    let inv = AgentInventory.Flat [ "a"; "b" ]
    Json.serialize inv |> should equal "[\"a\",\"b\"]"

[<Fact>]
let ``lease serializes as bare namespace map`` () =
    let lease = { Capabilities = Map.ofList [ "fs.read", [ "/workspace/**" ] ] }
    Json.serialize lease |> should equal "{\"fs.read\":[\"/workspace/**\"]}"

[<Fact>]
let ``credential constraints use dotted lease keys`` () =
    let c: CredentialConstraints =
        {
            CostBudget = Some [ "USD:5.00" ]
            ModelUse = Some [ "tier-fast/*" ]
            ExpiresAt = None
        }

    let json = Json.serialize c
    json |> should haveSubstring "\"cost.budget\":[\"USD:5.00\"]"
    json |> should haveSubstring "\"model.use\":[\"tier-fast/*\"]"

[<Fact>]
let ``job.event round-trips through wire JSON`` () =
    let payload: JobEventPayload =
        {
            Kind = "metric"
            Ts = ts
            Body = JobEventBody.Metric("cost.inference", 0.42m, Some "USD", Some(Map.ofList [ "model", "gpt" ]))
        }

    let json = Json.serialize payload
    let rt = Json.deserialize<JobEventPayload> json

    match rt.Body with
    | JobEventBody.Metric(name, value, unit, dims) ->
        name |> should equal "cost.inference"
        value |> should equal 0.42m
        unit |> should equal (Some "USD")
        dims |> should equal (Some(Map.ofList [ "model", "gpt" ]))
    | _ -> failwith "wrong body"

[<Fact>]
let ``unknown event kind round-trips via x-vendor`` () =
    let body = Json.parseElement "{\"foo\":1}"

    let payload: JobEventPayload =
        {
            Kind = "x-acme.custom"
            Ts = ts
            Body = JobEventBody.XVendor("x-acme.custom", body)
        }

    let json = Json.serialize payload
    json |> should haveSubstring "\"body\":{\"foo\":1}"
    let rt = Json.deserialize<JobEventPayload> json

    match rt.Body with
    | JobEventBody.XVendor(k, _) -> k |> should equal "x-acme.custom"
    | _ -> failwith "wrong body"
