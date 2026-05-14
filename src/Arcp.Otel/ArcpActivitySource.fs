namespace ARCP.Otel

open System.Diagnostics

/// Shared `ActivitySource` for ARCP-emitted spans (spec §11).
module ArcpActivitySource =
    [<Literal>]
    let Name = "ARCP"

    let Instance = new ActivitySource(Name, "1.0.0")

/// Canonical attribute keys for ARCP spans (spec §11).
module ArcpSpanAttributes =
    [<Literal>]
    let SessionId = "arcp.session_id"

    [<Literal>]
    let JobId = "arcp.job_id"

    [<Literal>]
    let Agent = "arcp.agent"

    [<Literal>]
    let LeaseCapabilities = "arcp.lease.capabilities"

    [<Literal>]
    let LeaseExpiresAt = "arcp.lease.expires_at"

    [<Literal>]
    let BudgetRemaining = "arcp.budget.remaining"
