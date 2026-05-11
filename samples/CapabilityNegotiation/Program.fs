/// Capability-driven peer routing with ordered fallback + cost rollup.
module ARCP.Samples.CapabilityNegotiation.Program

open System.Collections.Concurrent
open System.Text.Json
open System.Threading.Tasks
open ARCP.Client
open ARCP.Envelope
open ARCP.Errors
open ARCP.Trace

let peers = [ "anthropic-haiku"; "anthropic-sonnet"; "openai-4o"; "groq-llama" ]

let fallbackChains: Map<string, string list> =
    Map.ofList
        [
            "cheap_fast", [ "groq-llama"; "anthropic-haiku"; "openai-4o" ]
            "balanced", [ "anthropic-sonnet"; "openai-4o"; "anthropic-haiku" ]
            "deep", [ "anthropic-sonnet" ]
        ]

let costCeilingUsdPerMtok = 8.0
let latencyCeilingMs = 800

/// Errors that should fall through to the next peer in the chain.
let isRetryable (err: ARCPError) : bool =
    match err with
    | ResourceExhausted _
    | Unavailable _
    | DeadlineExceeded _
    | Aborted _ -> true
    | _ -> false

type Profile =
    {
        CostPerMtok: float
        P50LatencyMs: int
        ModelClass: string
    }

/// Capabilities is `extra="allow"` so namespaced fields ride alongside core booleans.
/// NOTE: §21 covers extension *messages*, not extension *capability values* — convention.
let profileFrom (caps: JsonElement) : Profile =
    let getF k def =
        match caps.TryGetProperty k with
        | true, v -> v.GetDouble()
        | _ -> def

    let getI k def =
        match caps.TryGetProperty k with
        | true, v -> v.GetInt32()
        | _ -> def

    let getS k def =
        match caps.TryGetProperty k with
        | true, v -> v.GetString()
        | _ -> def

    {
        CostPerMtok = getF "arcpx.market.cost_per_mtok.v1" 0.0
        P50LatencyMs = getI "arcpx.market.p50_latency_ms.v1" 0
        ModelClass = getS "arcpx.market.model_class.v1" "unknown"
    }

let candidateChain (profiles: Map<string, Profile>) (requestClass: string) : string list =
    fallbackChains
    |> Map.tryFind requestClass
    |> Option.defaultValue []
    |> List.filter (fun name ->
        match Map.tryFind name profiles with
        | Some p -> p.CostPerMtok <= costCeilingUsdPerMtok && p.P50LatencyMs <= latencyCeilingMs
        | None -> false)

/// Walk the chain. Retryable error → next peer; otherwise raise.
let invokeWithFallback
    (clients: Map<string, Client>)
    (chain: string list)
    (tool: string)
    (arguments: JsonElement)
    (traceId: TraceId)
    : Task<JsonElement option> =
    task {
        let mutable last: ARCPError option = None
        let mutable result: JsonElement option = None
        let mutable i = 0
        let arr = chain |> List.toArray

        while result.IsNone && i < arr.Length do
            let name = arr.[i]
            let client = clients.[name]
            i <- i + 1

            // Real call: client.InvokeAsync(tool, arguments, traceId, extensions = {"arcpx.market.peer.v1": name})
            let! r = client.InvokeAsync(tool, arguments)

            match r with
            | Ok v -> result <- Some(Option.defaultWith (fun () -> JsonDocument.Parse("null").RootElement) v)
            | Error e ->
                last <- Some e

                if not (isRetryable e) then
                    raise (exn (ARCPError.message e))

        match result with
        | Some v -> return Some v
        | None ->
            match last with
            | Some e -> return raise (exn (ARCPError.message e))
            | None -> return raise (exn "no peers available")
    }

type Usage =
    {
        mutable TokensIn: int
        mutable TokensOut: int
        mutable CostUsd: float
        ByPeer: ConcurrentDictionary<string, float>
    }

let mkUsage () =
    {
        TokensIn = 0
        TokensOut = 0
        CostUsd = 0.0
        ByPeer = ConcurrentDictionary()
    }

/// Roll up `metric` envelopes into per-tenant Usage.
let consumeMetric (env: Envelope<JsonElement>) (totals: ConcurrentDictionary<string, Usage>) : unit =
    if env.Type <> "metric" then
        ()
    else
        let p = env.Payload
        let dims = p.GetProperty("dims")
        let tenant = dims.GetProperty("tenant").GetString()
        let u = totals.GetOrAdd(tenant, fun _ -> mkUsage ())
        let name = p.GetProperty("name").GetString()
        let value = p.GetProperty("value").GetDouble()

        match name with
        | "tokens.used" ->
            match dims.GetProperty("kind").GetString() with
            | "input" -> u.TokensIn <- u.TokensIn + int value
            | "output" -> u.TokensOut <- u.TokensOut + int value
            | _ -> ()
        | "cost.usd" ->
            u.CostUsd <- u.CostUsd + value
            let peer = dims.GetProperty("peer").GetString()
            u.ByPeer.AddOrUpdate(peer, value, fun _ old -> old + value) |> ignore
        | _ -> ()

[<EntryPoint>]
let main _ =
    task {
        let mutable clients: Map<string, Client> = Map.empty
        let mutable profiles: Map<string, Profile> = Map.empty

        for name in peers do
            let c: Client = Unchecked.defaultof<_> // transport per peer URL, identity, auth elided
            clients <- clients.Add(name, c)
            // profiles <- profiles.Add(name, profileFrom c.NegotiatedCapabilities)
            ()

        let totals = ConcurrentDictionary<string, Usage>()
        let chain = candidateChain profiles "balanced"

        let traceId = TraceId.create ()

        let! _reply =
            invokeWithFallback
                clients
                chain
                "chat.completion"
                (JsonDocument.Parse("""{"prompt":"Hello","tenant":"acme-corp"}""").RootElement)
                traceId

        printfn "tenants metered: %d" totals.Count
        return ()
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
