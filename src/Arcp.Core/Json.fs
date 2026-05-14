namespace ARCP.Core

open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Encodings.Web

/// JSON configuration shared across the SDK.
///
/// The wire format (spec §5.1, §6, §7, §8) puts the discriminator as
/// a top-level `type` field next to peer fields. Use
/// `JsonFSharpOptions.InternalTag` keyed on `"type"`, not the default
/// `AdjacentTag` which would wrap as `{ "type": "X", "fields": {...} }`
/// and break the wire shape.
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
        opts

    /// Default `JsonSerializerOptions` for ARCP-wire serialisation.
    let Options : JsonSerializerOptions = buildOptions ()

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

    let nullElement () : JsonElement =
        parseElement "null"
