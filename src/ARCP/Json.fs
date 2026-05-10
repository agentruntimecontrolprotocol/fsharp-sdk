namespace ARCP

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.Json.Nodes
open System.Text.Encodings.Web

/// <summary>
/// JSON configuration for the ARCP wire format. <see cref="FSharp.SystemTextJson"/>
/// bridges F# DUs, options, records, and lists with <c>System.Text.Json</c>.
///
/// We use the <c>AdjacentTag</c> encoding for the <c>MessageType</c> DU
/// (discriminator field <c>type</c>; payload field <c>payload</c>), and the
/// <c>BareFieldlessTags</c> bare-value encoding for single-case DU id types.
/// </summary>
module Json =

    /// <summary>
    /// Default <see cref="JsonSerializerOptions"/> for ARCP wire payloads.
    /// </summary>
    let options: JsonSerializerOptions =
        let opts = JsonSerializerOptions()
        opts.PropertyNamingPolicy <- JsonNamingPolicy.SnakeCaseLower
        opts.DictionaryKeyPolicy <- JsonNamingPolicy.SnakeCaseLower
        opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        opts.Encoder <- JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        opts.Converters.Add(JsonStringEnumConverter())

        let fsharpOpts =
            JsonFSharpOptions
                .Default()
                .WithUnionTagName("type")
                .WithUnionTagCaseInsensitive(false)
                .WithUnionEncoding(
                    JsonUnionEncoding.AdjacentTag
                    ||| JsonUnionEncoding.NamedFields
                    ||| JsonUnionEncoding.UnwrapOption
                    ||| JsonUnionEncoding.UnwrapSingleCaseUnions
                )
                .WithUnionFieldsName("payload")
                .WithUnionTagNamingPolicy(JsonNamingPolicy.SnakeCaseLower)
                .WithSkippableOptionFields(SkippableOptionFields.Always)

        fsharpOpts.AddToJsonSerializerOptions(opts)
        opts

    /// <summary>Serialize a value to a JSON string using ARCP options.</summary>
    let serialize<'T> (value: 'T) : string =
        JsonSerializer.Serialize<'T>(value, options)

    /// <summary>Deserialize a value from a JSON string using ARCP options.</summary>
    let deserialize<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, options)

    /// <summary>Round-trip a value to a <see cref="JsonElement"/>.</summary>
    let toElement<'T> (value: 'T) : JsonElement =
        JsonSerializer.SerializeToElement<'T>(value, options)

    /// <summary>Read a value from a <see cref="JsonElement"/>.</summary>
    let fromElement<'T> (element: JsonElement) : 'T = element.Deserialize<'T>(options)

    /// <summary>Parse JSON text to a <see cref="JsonNode"/> for low-level inspection.</summary>
    let parseNode (json: string) : JsonNode = JsonNode.Parse(json)
