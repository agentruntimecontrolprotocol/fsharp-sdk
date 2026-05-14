namespace ARCP.Core

open System
open System.Globalization
open System.Text.RegularExpressions

/// An immutable lease grant (spec §9.1).
///
/// A lease is a map from capability namespace to a list of glob
/// patterns. `cost.budget` patterns are amount strings of the form
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

    /// Validate that `child` is a subset of `parent` (spec §9.4).
    /// For every namespace in `child`, every glob in `child` must be
    /// covered by some glob in `parent`. A `child` namespace that
    /// `parent` does not grant is a violation.
    ///
    /// Budget subsetting (v1.1, §9.4): a child's `cost.budget` MUST
    /// NOT exceed the parent's *remaining* budget per currency. The
    /// caller supplies the parent's remaining budget.
    /// Expiry subsetting: a child's `expires_at` MUST NOT exceed the
    /// parent's.
    let isSubset
        (child: LeaseGrant)
        (parent: LeaseGrant)
        (parentRemainingBudget: Map<string, decimal>)
        (parentExpiresAt: DateTimeOffset option)
        (childExpiresAt: DateTimeOffset option)
        : Result<unit, ARCPError> =
        // Per-namespace subset check.
        let nsResult =
            child.Capabilities
            |> Map.toSeq
            |> Seq.tryPick (fun (ns, childGlobs) ->
                match Map.tryFind ns parent.Capabilities with
                | None ->
                    Some (
                        ARCPError.LeaseSubsetViolation(
                            sprintf "Child lease has namespace %s not in parent" ns,
                            None
                        )
                    )
                | Some parentGlobs ->
                    if ns = Capabilities.CostBudget then
                        // Budget subset is amount-vs-remaining, checked below.
                        None
                    else
                        childGlobs
                        |> List.tryPick (fun cg ->
                            if parentGlobs |> List.exists (fun pg -> pg = cg || pg = "**") then
                                None
                            else
                                Some (
                                    ARCPError.LeaseSubsetViolation(
                                        sprintf "Child glob %s in %s not covered by parent" cg ns,
                                        None
                                    )
                                )))

        match nsResult with
        | Some err -> Error err
        | None ->
            // Budget subset: child's per-currency budget ≤ parent's remaining.
            let budgetResult =
                child.Capabilities
                |> Map.tryFind Capabilities.CostBudget
                |> Option.bind (fun amts ->
                    let childMap =
                        amts
                        |> List.choose (fun a ->
                            match parseBudgetAmount a with
                            | Ok kv -> Some kv
                            | Error _ -> None)
                        |> Map.ofList
                    childMap
                    |> Map.toSeq
                    |> Seq.tryPick (fun (currency, requested) ->
                        let remaining =
                            Map.tryFind currency parentRemainingBudget |> Option.defaultValue 0m
                        if requested > remaining then
                            Some (
                                ARCPError.LeaseSubsetViolation(
                                    sprintf
                                        "Child cost.budget %s:%O exceeds parent remaining %O"
                                        currency
                                        requested
                                        remaining,
                                    None
                                )
                            )
                        else
                            None))

            match budgetResult with
            | Some err -> Error err
            | None ->
                // Expiry subset.
                match childExpiresAt, parentExpiresAt with
                | Some c, Some p when c > p ->
                    Error (
                        ARCPError.LeaseSubsetViolation(
                            sprintf "Child expires_at %O exceeds parent %O" c p,
                            None
                        )
                    )
                | _ -> Ok ()

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
