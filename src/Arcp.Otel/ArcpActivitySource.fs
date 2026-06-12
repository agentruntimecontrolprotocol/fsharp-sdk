namespace ARCP.Otel

open System.Diagnostics
open ARCP.Core

/// Shared `ActivitySource` for ARCP-emitted spans (spec §11).
module ArcpActivitySource =
    [<Literal>]
    let Name = "ARCP"

    /// Lazily-created shared source (#42): not allocated at module load
    /// and versioned from `Version.Sdk` so the version has one source of
    /// truth. Access the source via `Instance.Value`.
    let Instance = lazy (new ActivitySource(Name, Version.Sdk))

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
