namespace ARCP.Core

open System
open System.Globalization
open System.Text.RegularExpressions

/// An immutable lease grant (spec §9.1).
///
/// A map from capability namespace to a list of glob patterns.
/// `cost.budget` patterns are amount strings of the form
/// `currency:decimal` (§9.6).
type LeaseGrant = {
    Capabilities: Map<string, string list>
}

/// Optional time-bound on a lease (spec §9.5).
type LeaseConstraints = {
    /// ISO 8601 UTC ('Z'); MUST be in the future at submit time.
    ExpiresAt: DateTimeOffset
}

/// Reserved capability namespaces (spec §9.2).
[<RequireQualifiedAccess>]
module Capabilities =
    [<Literal>]
    let FsRead = "fs.read"

    [<Literal>]
    let FsWrite = "fs.write"

    [<Literal>]
    let NetFetch = "net.fetch"

    [<Literal>]
    let ToolCall = "tool.call"

    [<Literal>]
    let AgentDelegate = "agent.delegate"

    [<Literal>]
    let CostBudget = "cost.budget"

[<RequireQualifiedAccess>]
module Glob =
    /// Compile a glob pattern (`?`, `*`, `**`) into a regex.
    /// `**` matches any path segment including `/`; `*` matches
    /// any character except `/`; `?` matches a single non-`/` char.
    let compile (pattern: string) : Regex =
        let sb = System.Text.StringBuilder()
        sb.Append "^" |> ignore
        let mutable i = 0
        let n = pattern.Length
        while i < n do
            let c = pattern.[i]
            if c = '*' && i + 1 < n && pattern.[i + 1] = '*' then
                sb.Append ".*" |> ignore
                i <- i + 2
            elif c = '*' then
                sb.Append "[^/]*" |> ignore
                i <- i + 1
            elif c = '?' then
                sb.Append "[^/]" |> ignore
                i <- i + 1
            else
                sb.Append(Regex.Escape(string c)) |> ignore
                i <- i + 1
        sb.Append "$" |> ignore
        Regex(sb.ToString(), RegexOptions.Compiled ||| RegexOptions.CultureInvariant)

    let isMatch (pattern: string) (target: string) : bool =
        // Amount strings used in `cost.budget` and any non-glob
        // pattern can match by string equality.
        if pattern = target then true
        else (compile pattern).IsMatch target

[<RequireQualifiedAccess>]
module Lease =
    let empty : LeaseGrant = { Capabilities = Map.empty }

    let withCapability (ns: string) (globs: string list) (lease: LeaseGrant) : LeaseGrant =
        { lease with Capabilities = Map.add ns globs lease.Capabilities }

    /// Does the lease grant `capability` on `target`?
    let matches (lease: LeaseGrant) (capability: string) (target: string) : bool =
        match Map.tryFind capability lease.Capabilities with
        | None -> false
        | Some globs -> globs |> List.exists (fun g -> Glob.isMatch g target)

    /// Parse a `cost.budget` amount string (`currency:decimal`).
    let parseBudgetAmount (amount: string) : Result<string * decimal, string> =
        let idx = amount.IndexOf ':'
        if idx <= 0 || idx = amount.Length - 1 then
            Error (sprintf "Invalid cost.budget amount: %s" amount)
        else
            let currency = amount.Substring(0, idx)
            let value = amount.Substring(idx + 1)
            match Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture) with
            | true, d when d >= 0m -> Ok (currency, d)
            | _ -> Error (sprintf "Invalid cost.budget amount: %s" amount)

    /// Initial budget counters from a lease.
    let initialBudgets (lease: LeaseGrant) : Map<string, decimal> =
        match Map.tryFind Capabilities.CostBudget lease.Capabilities with
        | None -> Map.empty
        | Some amounts ->
            amounts
            |> List.choose (fun a ->
                match parseBudgetAmount a with
                | Ok kv -> Some kv
                | Error _ -> None)
            |> List.groupBy fst
            |> List.map (fun (c, xs) -> c, xs |> List.sumBy snd)
            |> Map.ofList

    let private violation (msg: string) : ARCPError =
        ARCPError.LeaseSubsetViolation(msg, None)

    let private checkNamespace
            (parent: LeaseGrant)
            ((ns: string), (childGlobs: string list))
            : ARCPError option =
        match Map.tryFind ns parent.Capabilities with
        | None -> Some (violation (sprintf "Child lease has namespace %s not in parent" ns))
        | Some _ when ns = Capabilities.CostBudget -> None
        | Some parentGlobs ->
            childGlobs
            |> List.tryPick (fun cg ->
                if parentGlobs |> List.exists (fun pg -> pg = cg || pg = "**") then None
                else Some (violation (sprintf "Child glob %s in %s not covered by parent" cg ns)))

    let private subsetNamespaces (child: LeaseGrant) (parent: LeaseGrant) : ARCPError option =
        child.Capabilities
        |> Map.toSeq
        |> Seq.tryPick (checkNamespace parent)

    let private subsetBudget
            (child: LeaseGrant)
            (parentRemaining: Map<string, decimal>)
            : ARCPError option =
        child.Capabilities
        |> Map.tryFind Capabilities.CostBudget
        |> Option.bind (fun amts ->
            amts
            |> List.choose (fun a ->
                match parseBudgetAmount a with
                | Ok kv -> Some kv
                | Error _ -> None)
            |> Map.ofList
            |> Map.toSeq
            |> Seq.tryPick (fun (currency, requested) ->
                let remaining =
                    Map.tryFind currency parentRemaining |> Option.defaultValue 0m
                if requested > remaining then
                    Some (violation (
                        sprintf "Child cost.budget %s:%O exceeds parent remaining %O"
                            currency requested remaining))
                else None))

    let private subsetExpiry
            (parentExpiresAt: DateTimeOffset option)
            (childExpiresAt: DateTimeOffset option)
            : ARCPError option =
        match childExpiresAt, parentExpiresAt with
        | Some c, Some p when c > p ->
            Some (violation (sprintf "Child expires_at %O exceeds parent %O" c p))
        | _ -> None

    /// Validate that `child` is a subset of `parent` (spec §9.4).
    ///
    /// Three checks run in order — namespace coverage, per-currency
    /// budget vs parent's remaining, then expiry. The first failure
    /// short-circuits.
    let isSubset
            (child: LeaseGrant)
            (parent: LeaseGrant)
            (parentRemainingBudget: Map<string, decimal>)
            (parentExpiresAt: DateTimeOffset option)
            (childExpiresAt: DateTimeOffset option)
            : Result<unit, ARCPError> =
        match subsetNamespaces child parent with
        | Some e -> Error e
        | None ->
            match subsetBudget child parentRemainingBudget with
            | Some e -> Error e
            | None ->
                match subsetExpiry parentExpiresAt childExpiresAt with
                | Some e -> Error e
                | None -> Ok ()

    /// Stateless authorisation check. Order: namespace+glob match,
    /// then expiry (§9.5), then per-currency budget counter (§9.6).
    /// The first failure short-circuits.
    let validateLeaseOp
        (lease: LeaseGrant)
        (constraints: LeaseConstraints option)
        (budgets: Map<string, decimal>)
        (now: DateTimeOffset)
        (capability: string)
        (target: string)
        : Result<unit, ARCPError> =
        if not (matches lease capability target) then
            Error (ARCPError.PermissionDenied(
                sprintf "Operation %s on %s denied by lease" capability target, None))
        else
            match constraints with
            | Some c when now >= c.ExpiresAt -> Error (ARCPError.LeaseExpired c.ExpiresAt)
            | _ ->
                // Budget check: if any counter is ≤ 0, deny.
                let exhausted =
                    budgets
                    |> Map.toSeq
                    |> Seq.tryFind (fun (_, v) -> v <= 0m)
                match exhausted with
                | Some (currency, _) -> Error (ARCPError.BudgetExhausted currency)
                | None -> Ok ()
