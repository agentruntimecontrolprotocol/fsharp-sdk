namespace ARCP.Otel

open System.Diagnostics
open ARCP.Core

/// Helpers that wrap a job's lifetime in an OpenTelemetry span.
///
/// The runtime can call `BeginJobSpan` at `job.submit` time and
/// dispose the returned activity at terminal status, tagging the
/// span with the canonical ARCP attributes.
[<RequireQualifiedAccess>]
module ArcpOtel =
    let beginJobSpan
            (sessionId: SessionId)
            (jobId: JobId)
            (agent: string)
            (lease: LeaseGrant)
            (constraints: LeaseConstraints option) : Activity option =
        let activity =
            ArcpActivitySource.Instance.StartActivity(
                "arcp.job",
                ActivityKind.Internal)
        match activity with
        | null -> None
        | a ->
            a.SetTag(ArcpSpanAttributes.SessionId, sessionId.Value) |> ignore
            a.SetTag(ArcpSpanAttributes.JobId, jobId.Value) |> ignore
            a.SetTag(ArcpSpanAttributes.Agent, agent) |> ignore
            a.SetTag(
                ArcpSpanAttributes.LeaseCapabilities,
                lease.Capabilities |> Map.toSeq |> Seq.map fst |> String.concat ",") |> ignore
            match constraints with
            | Some c -> a.SetTag(ArcpSpanAttributes.LeaseExpiresAt, c.ExpiresAt.ToString("O")) |> ignore
            | None -> ()
            Some a

    let recordBudgetRemaining (activity: Activity) (currency: string) (remaining: decimal) : unit =
        let key = ArcpSpanAttributes.BudgetRemaining + "." + currency
        activity.SetTag(key, remaining) |> ignore
