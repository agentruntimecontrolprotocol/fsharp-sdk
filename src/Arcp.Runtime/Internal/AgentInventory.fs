namespace ARCP.Runtime.Internal

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ARCP.Core

/// Function an agent registers to handle a job (spec §7.1).
///
/// Receives a `JobContext` (defined in `ARCP.Runtime`) and the
/// `input` JsonElement; returns the result payload. Cancellation
/// flows through `JobContext.CancellationToken`.
type internal AgentHandler = obj -> Task<JsonElement>

/// `name@version` parser per spec §7.5 grammar.
[<RequireQualifiedAccess>]
module internal AgentRef =
    let parse (raw: string) : string * string option =
        match raw.IndexOf '@' with
        | -1 -> raw, None
        | idx ->
            let name = raw.Substring(0, idx)
            let version = raw.Substring(idx + 1)
            name, Some version

    let format (name: string) (version: string option) : string =
        match version with
        | Some v -> name + "@" + v
        | None -> name

/// Versioned agent registry. Spec §7.5: `name@version` is exact;
/// bare `name` resolves to the registered default; if no default,
/// the runtime MAY pick any version, but pinning is recommended.
type internal AgentInventoryStore() =
    let byName = ConcurrentDictionary<string, ConcurrentDictionary<string, AgentHandler>>()
    let defaults = ConcurrentDictionary<string, string>()

    member _.Register(name: string, version: string, handler: AgentHandler) : unit =
        let versions = byName.GetOrAdd(name, fun _ -> ConcurrentDictionary<string, AgentHandler>())
        versions.[version] <- handler
        // First version becomes the default if none set yet.
        defaults.TryAdd(name, version) |> ignore

    member _.SetDefault(name: string, version: string) : unit =
        defaults.[name] <- version

    /// Resolve `agent` (either `name` or `name@version`) to a
    /// concrete `(name, version, handler)` triple.
    member _.Resolve(agent: string) : Result<string * string * AgentHandler, ARCPError> =
        let name, requested = AgentRef.parse agent
        match byName.TryGetValue name with
        | false, _ -> Error (ARCPError.AgentNotAvailable name)
        | true, versions ->
            match requested with
            | Some v ->
                match versions.TryGetValue v with
                | true, h -> Ok (name, v, h)
                | _ -> Error (ARCPError.AgentVersionNotAvailable(name, v))
            | None ->
                match defaults.TryGetValue name with
                | true, v ->
                    match versions.TryGetValue v with
                    | true, h -> Ok (name, v, h)
                    | _ -> Error (ARCPError.AgentNotAvailable name)
                | _ ->
                    // Pick any version present.
                    versions
                    |> Seq.tryHead
                    |> Option.map (fun kv -> Ok (name, kv.Key, kv.Value))
                    |> Option.defaultValue (Error (ARCPError.AgentNotAvailable name))

    /// Build the rich agent inventory shape for `session.welcome`.
    member _.ToRichInventory() : AgentInventoryEntry list =
        byName
        |> Seq.map (fun kvp ->
            let versions = kvp.Value.Keys |> Seq.toList |> List.sort
            let dflt =
                match defaults.TryGetValue kvp.Key with
                | true, v -> Some v
                | _ -> None
            { Name = kvp.Key; Versions = versions; Default = dflt })
        |> Seq.toList

    /// Build the flat inventory for v1.0-compat clients that
    /// haven't negotiated `agent_versions`.
    member _.ToFlatInventory() : string list =
        byName.Keys |> Seq.toList |> List.sort
