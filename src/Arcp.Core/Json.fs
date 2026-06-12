namespace ARCP.Core

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Encodings.Web

/// Custom converters that pin the spec wire format (§6.2, §7.3,
/// §8.2, §8.4). FSharp.SystemTextJson's union handling would emit
/// case-name wrappers and PascalCase tags; these converters instead
/// produce the flat, lowercase, snake_case shapes the spec mandates
/// so non-F# peers (TS/Go SDKs, the spec examples) interoperate.
module internal JsonConverters =

    let private logLevelToWire (l: LogLevel) =
        match l with
        | LogLevel.Debug -> "debug"
        | LogLevel.Info -> "info"
        | LogLevel.Warn -> "warn"
        | LogLevel.Error -> "error"

    let private logLevelOfWire (s: string) =
        match s with
        | "debug" -> LogLevel.Debug
        | "info" -> LogLevel.Info
        | "warn" -> LogLevel.Warn
        | "error" -> LogLevel.Error
        | other -> raise (JsonException(sprintf "Unknown log level: %s" other))

    let private chunkEncodingToWire (e: ChunkEncoding) =
        match e with
        | ChunkEncoding.Utf8 -> "utf8"
        | ChunkEncoding.Base64 -> "base64"

    let private chunkEncodingOfWire (s: string) =
        match s with
        | "utf8" -> ChunkEncoding.Utf8
        | "base64" -> ChunkEncoding.Base64
        | other -> raise (JsonException(sprintf "Unknown chunk encoding: %s" other))

    type JobStatusConverter() =
        inherit JsonConverter<JobStatus>()

        override _.Read(reader, _, _) =
            let s = reader.GetString()

            match JobStatus.tryOfWire s with
            | Ok v -> v
            | Error e -> raise (JsonException e)

        override _.Write(writer, value, _) =
            writer.WriteStringValue(JobStatus.toWire value)

    type LogLevelConverter() =
        inherit JsonConverter<LogLevel>()
        override _.Read(reader, _, _) = logLevelOfWire (reader.GetString())

        override _.Write(writer, value, _) =
            writer.WriteStringValue(logLevelToWire value)

    type ChunkEncodingConverter() =
        inherit JsonConverter<ChunkEncoding>()

        override _.Read(reader, _, _) =
            chunkEncodingOfWire (reader.GetString())

        override _.Write(writer, value, _) =
            writer.WriteStringValue(chunkEncodingToWire value)

    /// `LeaseGrant` is wire-encoded as a bare namespace→patterns map
    /// (§9.1, §9.2), not a `{ "capabilities": {...} }` wrapper.
    type LeaseGrantConverter() =
        inherit JsonConverter<LeaseGrant>()

        override _.Read(reader, _, options) =
            let map = JsonSerializer.Deserialize<Map<string, string list>>(&reader, options)
            { Capabilities = map }

        override _.Write(writer, value, options) =
            JsonSerializer.Serialize(writer, value.Capabilities, options)

    /// Credential constraints use dotted lease-namespace keys
    /// (`cost.budget`, `model.use`) per §9.8.1, not snake_case.
    type CredentialConstraintsConverter() =
        inherit JsonConverter<CredentialConstraints>()

        override _.Read(reader, _, options) =
            let el = JsonElement.ParseValue(&reader)

            let prop name =
                match el.TryGetProperty(name: string) with
                | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
                | _ -> None

            {
                CostBudget =
                    prop "cost.budget"
                    |> Option.map (fun v -> JsonSerializer.Deserialize<string list>(v.GetRawText(), options))
                ModelUse =
                    prop "model.use"
                    |> Option.map (fun v -> JsonSerializer.Deserialize<string list>(v.GetRawText(), options))
                ExpiresAt = prop "expires_at" |> Option.map (fun v -> v.GetDateTimeOffset())
            }

        override _.Write(writer, value, options) =
            writer.WriteStartObject()

            value.CostBudget
            |> Option.iter (fun xs ->
                writer.WritePropertyName("cost.budget")
                JsonSerializer.Serialize(writer, xs, options))

            value.ModelUse
            |> Option.iter (fun xs ->
                writer.WritePropertyName("model.use")
                JsonSerializer.Serialize(writer, xs, options))

            value.ExpiresAt
            |> Option.iter (fun t ->
                writer.WritePropertyName("expires_at")
                JsonSerializer.Serialize(writer, t, options))

            writer.WriteEndObject()

    /// `capabilities.agents` is a plain JSON array in both the flat
    /// (string) and rich (object) shapes (§6.2).
    type AgentInventoryConverter() =
        inherit JsonConverter<AgentInventory>()

        override _.Read(reader, _, options) =
            let el = JsonElement.ParseValue(&reader)

            if el.ValueKind <> JsonValueKind.Array then
                raise (JsonException "capabilities.agents must be an array")

            let items = el.EnumerateArray() |> Seq.toList

            match items with
            | [] -> AgentInventory.Flat []
            | first :: _ when first.ValueKind = JsonValueKind.String ->
                AgentInventory.Flat(items |> List.map (fun e -> e.GetString()))
            | _ ->
                AgentInventory.Rich(
                    items
                    |> List.map (fun e -> JsonSerializer.Deserialize<AgentInventoryEntry>(e.GetRawText(), options))
                )

        override _.Write(writer, value, options) =
            writer.WriteStartArray()

            match value with
            | AgentInventory.Flat names -> names |> List.iter writer.WriteStringValue
            | AgentInventory.Rich entries ->
                entries |> List.iter (fun e -> JsonSerializer.Serialize(writer, e, options))

            writer.WriteEndArray()

    let private writeBody (writer: Utf8JsonWriter) (options: JsonSerializerOptions) (body: JobEventBody) =
        let writeOptStr name (v: string option) =
            v |> Option.iter (fun s -> writer.WriteString((name: string), s))

        match body with
        | JobEventBody.XVendor(_, el) -> el.WriteTo(writer)
        | _ ->
            writer.WriteStartObject()

            match body with
            | JobEventBody.XVendor _ -> ()
            | JobEventBody.Log(level, message) ->
                writer.WriteString("level", logLevelToWire level)
                writer.WriteString("message", message)
            | JobEventBody.Thought text -> writer.WriteString("text", text)
            | JobEventBody.ToolCall(tool, args, callId) ->
                writer.WriteString("tool", tool)
                writer.WritePropertyName("args")
                args.WriteTo(writer)
                writer.WriteString("call_id", callId)
            | JobEventBody.ToolResult(callId, outcome) ->
                writer.WriteString("call_id", callId)

                match outcome with
                | ToolOutcome.Result v ->
                    writer.WritePropertyName("result")
                    v.WriteTo(writer)
                | ToolOutcome.Error(code, message, retryable) ->
                    writer.WritePropertyName("error")
                    writer.WriteStartObject()
                    writer.WriteString("code", code)
                    writer.WriteString("message", message)
                    writer.WriteBoolean("retryable", retryable)
                    writer.WriteEndObject()
            | JobEventBody.Status(phase, message) ->
                writer.WriteString("phase", phase)
                writeOptStr "message" message
            | JobEventBody.Metric(name, value, unit, dimensions) ->
                writer.WriteString("name", name)
                writer.WriteNumber("value", value)
                writeOptStr "unit" unit

                dimensions
                |> Option.iter (fun d ->
                    writer.WritePropertyName("dimensions")
                    JsonSerializer.Serialize(writer, d, options))
            | JobEventBody.ArtifactRef(uri, contentType, byteSize, sha256) ->
                writer.WriteString("uri", uri)
                writer.WriteString("content_type", contentType)
                byteSize |> Option.iter (fun b -> writer.WriteNumber("byte_size", b))
                writeOptStr "sha256" sha256
            | JobEventBody.Delegate b ->
                writer.WriteString("child_job_id", b.ChildJobId)
                writer.WriteString("agent", b.Agent)
                writer.WritePropertyName("lease")
                JsonSerializer.Serialize(writer, b.Lease, options)

                b.LeaseConstraints
                |> Option.iter (fun lc ->
                    writer.WritePropertyName("lease_constraints")
                    JsonSerializer.Serialize(writer, lc, options))
            | JobEventBody.Progress(current, total, units, message) ->
                writer.WriteNumber("current", current)
                total |> Option.iter (fun t -> writer.WriteNumber("total", t))
                writeOptStr "units" units
                writeOptStr "message" message
            | JobEventBody.ResultChunk(resultId, chunkSeq, data, encoding, more) ->
                writer.WriteString("result_id", resultId)
                writer.WriteNumber("chunk_seq", chunkSeq)
                writer.WriteString("data", data)
                writer.WriteString("encoding", chunkEncodingToWire encoding)
                writer.WriteBoolean("more", more)

            writer.WriteEndObject()

    let private readBody (kind: string) (el: JsonElement) (options: JsonSerializerOptions) : JobEventBody =
        let req name = el.GetProperty(name: string)

        let opt name =
            match el.TryGetProperty(name: string) with
            | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
            | _ -> None

        let optStr name =
            opt name |> Option.map (fun v -> v.GetString())

        match kind with
        | "log" -> JobEventBody.Log(logLevelOfWire ((req "level").GetString()), (req "message").GetString())
        | "thought" -> JobEventBody.Thought((req "text").GetString())
        | "tool_call" ->
            JobEventBody.ToolCall((req "tool").GetString(), (req "args").Clone(), (req "call_id").GetString())
        | "tool_result" ->
            let callId = (req "call_id").GetString()

            let outcome =
                match opt "error" with
                | Some errEl ->
                    ToolOutcome.Error(
                        (errEl.GetProperty("code")).GetString(),
                        (errEl.GetProperty("message")).GetString(),
                        (errEl.GetProperty("retryable")).GetBoolean()
                    )
                | None -> ToolOutcome.Result((req "result").Clone())

            JobEventBody.ToolResult(callId, outcome)
        | "status" -> JobEventBody.Status((req "phase").GetString(), optStr "message")
        | "metric" ->
            JobEventBody.Metric(
                (req "name").GetString(),
                (req "value").GetDecimal(),
                optStr "unit",
                opt "dimensions"
                |> Option.map (fun v -> JsonSerializer.Deserialize<Map<string, string>>(v.GetRawText(), options))
            )
        | "artifact_ref" ->
            JobEventBody.ArtifactRef(
                (req "uri").GetString(),
                (req "content_type").GetString(),
                opt "byte_size" |> Option.map (fun v -> v.GetInt64()),
                optStr "sha256"
            )
        | "delegate" ->
            let b: DelegateBody =
                {
                    ChildJobId = (req "child_job_id").GetString()
                    Agent = (req "agent").GetString()
                    Lease = JsonSerializer.Deserialize<LeaseGrant>((req "lease").GetRawText(), options)
                    LeaseConstraints =
                        opt "lease_constraints"
                        |> Option.map (fun v -> JsonSerializer.Deserialize<LeaseConstraints>(v.GetRawText(), options))
                }

            JobEventBody.Delegate b
        | "progress" ->
            JobEventBody.Progress(
                (req "current").GetDecimal(),
                opt "total" |> Option.map (fun v -> v.GetDecimal()),
                optStr "units",
                optStr "message"
            )
        | "result_chunk" ->
            JobEventBody.ResultChunk(
                (req "result_id").GetString(),
                (req "chunk_seq").GetInt64(),
                (req "data").GetString(),
                chunkEncodingOfWire ((req "encoding").GetString()),
                (req "more").GetBoolean()
            )
        | other -> JobEventBody.XVendor(other, el.Clone())

    /// `job.event` payload converter: emits `{ kind, ts, body }` with
    /// `body` as the flat kind-specific shape (§8.1, §8.2).
    type JobEventPayloadConverter() =
        inherit JsonConverter<JobEventPayload>()

        override _.Read(reader, _, options) =
            let el = JsonElement.ParseValue(&reader)
            let kind = (el.GetProperty("kind")).GetString()
            let ts = (el.GetProperty("ts")).GetDateTimeOffset()
            let body = readBody kind (el.GetProperty("body")) options
            { Kind = kind; Ts = ts; Body = body }

        override _.Write(writer, value, options) =
            writer.WriteStartObject()
            writer.WriteString("kind", value.Kind)
            writer.WritePropertyName("ts")
            JsonSerializer.Serialize(writer, value.Ts, options)
            writer.WritePropertyName("body")
            writeBody writer options value.Body
            writer.WriteEndObject()

/// JSON configuration shared across the SDK.
///
/// The wire format (spec §5.1, §6, §7, §8) puts the discriminator as
/// a top-level `type` field next to peer fields. Custom converters in
/// `JsonConverters` pin the spec-mandated flat shapes for the unions
/// that appear inside payloads.
[<RequireQualifiedAccess>]
module Json =
    let private buildOptions () : JsonSerializerOptions =
        let opts =
            JsonFSharpOptions
                .Default()
                .WithUnionExternalTag()
                .WithUnionTagName("type")
                .WithUnionNamedFields()
                .WithUnionTagCaseInsensitive(false)
                .WithUnionUnwrapFieldlessTags()
                .WithUnionUnwrapSingleCaseUnions()
                .WithUnionUnwrapSingleFieldCases()
                .WithUnionUnwrapRecordCases()
                .WithSkippableOptionFields()
                .ToJsonSerializerOptions()

        opts.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
        opts.PropertyNameCaseInsensitive <- false
        opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        opts.WriteIndented <- false
        opts.Encoder <- JavaScriptEncoder.UnsafeRelaxedJsonEscaping

        // Wire-format converters take priority over the FSharp.SystemTextJson
        // union/record handling; insert them at the front.
        opts.Converters.Insert(0, JsonConverters.JobStatusConverter())
        opts.Converters.Insert(1, JsonConverters.LogLevelConverter())
        opts.Converters.Insert(2, JsonConverters.ChunkEncodingConverter())
        opts.Converters.Insert(3, JsonConverters.LeaseGrantConverter())
        opts.Converters.Insert(4, JsonConverters.CredentialConstraintsConverter())
        opts.Converters.Insert(5, JsonConverters.AgentInventoryConverter())
        opts.Converters.Insert(6, JsonConverters.JobEventPayloadConverter())
        opts

    /// Default `JsonSerializerOptions` for ARCP-wire serialisation.
    let Options: JsonSerializerOptions = buildOptions ()

    let inline serialize<'T> (value: 'T) : string =
        JsonSerializer.Serialize<'T>(value, Options)

    let inline serializeToElement<'T> (value: 'T) : JsonElement =
        JsonSerializer.SerializeToElement<'T>(value, Options)

    let inline deserialize<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, Options)

    let inline deserializeElement<'T> (element: JsonElement) : 'T =
        let bytes = element.GetRawText()
        JsonSerializer.Deserialize<'T>(bytes, Options)

    let parseElement (json: string) : JsonElement =
        let doc = JsonDocument.Parse(json)
        doc.RootElement.Clone()

    let nullElement () : JsonElement = parseElement "null"
