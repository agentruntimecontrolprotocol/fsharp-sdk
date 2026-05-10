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

    // STJ's generic deserialize APIs return `'T | null` (null is the parse of
    // the literal JSON `null`, which never appears in well-formed ARCP wire
    // payloads). `unwrap` makes the null case loud rather than silently
    // propagating a null reference.
    let inline private unwrap<'T when 'T: not null and 'T: not struct> (value: 'T | null) : 'T =
        match value with
        | null -> invalidOp (sprintf "deserialization produced null for %s" typeof<'T>.FullName)
        | v -> v

    /// <summary>Deserialize a value from a JSON string using ARCP options.</summary>
    let deserialize<'T when 'T: not null and 'T: not struct> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, options) |> unwrap

    /// <summary>Round-trip a value to a <see cref="JsonElement"/>.</summary>
    let toElement<'T> (value: 'T) : JsonElement =
        JsonSerializer.SerializeToElement<'T>(value, options)

    /// <summary>Read a value from a <see cref="JsonElement"/>.</summary>
    let fromElement<'T when 'T: not null and 'T: not struct> (element: JsonElement) : 'T =
        element.Deserialize<'T>(options) |> unwrap

    /// <summary>Parse JSON text to a <see cref="JsonNode"/> for low-level inspection.</summary>
    let parseNode (json: string) : JsonNode = JsonNode.Parse json |> unwrap
