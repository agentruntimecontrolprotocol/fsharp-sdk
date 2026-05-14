namespace ARCP.Runtime

open System
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Runtime.Internal

/// Context handed to an agent handler at job submit time
/// (spec §7.1, §8, §9).
///
/// The agent emits events through `EmitXxxAsync`, validates
/// operations against the lease via `ValidateOpAsync`, and watches
/// `CancellationToken` for cooperative cancellation.
type JobContext internal (
    jobId: JobId,
    sessionId: SessionId,
    lease: LeaseGrant,
    constraints: LeaseConstraints option,
    budgets: BudgetCounters,
    timeProvider: TimeProvider,
    cancellationToken: CancellationToken,
    emit: JobEventBody -> Task,
    streamResultBegin: unit -> ResultId,
    onCostMetric: string * decimal -> unit
) =
    member _.JobId : JobId = jobId
    member _.SessionId : SessionId = sessionId
    member _.Lease : LeaseGrant = lease
    member _.LeaseConstraints : LeaseConstraints option = constraints
    member _.CancellationToken : CancellationToken = cancellationToken

    /// Snapshot of remaining budget counters.
    member _.RemainingBudget : Map<string, decimal> = budgets.Snapshot()

    member _.EmitLogAsync(level: LogLevel, message: string, _ct: CancellationToken) : Task =
        emit (JobEventBody.Log(level, message))

    member _.EmitThoughtAsync(text: string, _ct: CancellationToken) : Task =
        emit (JobEventBody.Thought text)

    member _.EmitToolCallAsync(tool: string, args: JsonElement, callId: string, _ct: CancellationToken) : Task =
        emit (JobEventBody.ToolCall(tool, args, callId))

    member _.EmitToolResultAsync(callId: string, outcome: ToolOutcome, _ct: CancellationToken) : Task =
        emit (JobEventBody.ToolResult(callId, outcome))

    member _.EmitStatusAsync(phase: string, message: string option, _ct: CancellationToken) : Task =
        emit (JobEventBody.Status(phase, message))

    member _.EmitProgressAsync(
            current: decimal,
            total: decimal option,
            units: string option,
            message: string option,
            _ct: CancellationToken) : Task =
        emit (JobEventBody.Progress(current, total, units, message))

    /// Emit a `metric` event. Names starting with `cost.` and a
    /// budgeted `unit` decrement the matching budget counter
    /// (spec §9.6). Negative values are rejected per §9.6.
    member _.EmitMetricAsync(
            name: string,
            value: decimal,
            unit: string option,
            dimensions: Map<string, string> option,
            _ct: CancellationToken) : Task =
        if value < 0m then
            Task.CompletedTask
        else
            if name.StartsWith("cost.") then
                match unit with
                | Some u -> onCostMetric (u, value)
                | None -> ()
            emit (JobEventBody.Metric(name, value, unit, dimensions))

    member _.EmitArtifactRefAsync(
            uri: string,
            contentType: string,
            byteSize: int64 option,
            sha256: string option,
            _ct: CancellationToken) : Task =
        emit (JobEventBody.ArtifactRef(uri, contentType, byteSize, sha256))

    /// Begin streaming a chunked result. Returns the `result_id`
    /// the runtime generated; the agent then calls `EmitResultChunk`
    /// repeatedly until `more = false`. The terminating `job.result`
    /// MUST carry this `result_id`.
    member _.BeginStreamingResult() : ResultId =
        streamResultBegin ()

    member _.EmitResultChunkAsync(
            resultId: ResultId,
            chunkSeq: int64,
            data: ReadOnlyMemory<byte>,
            encoding: ChunkEncoding,
            more: bool,
            _ct: CancellationToken) : Task =
        let encoded =
            match encoding with
            | ChunkEncoding.Utf8 -> Encoding.UTF8.GetString(data.Span)
            | ChunkEncoding.Base64 -> Convert.ToBase64String(data.Span)
        emit (JobEventBody.ResultChunk(resultId.Value, chunkSeq, encoded, encoding, more))

    /// Validate a lease-bearing operation. Returns immediately if
    /// allowed; raises `ArcpException` with `PermissionDenied`,
    /// `LeaseExpired`, or `BudgetExhausted` otherwise.
    member _.ValidateOpAsync(capability: string, target: string, _ct: CancellationToken) : Task =
        let now = timeProvider.GetUtcNow()
        let budgetsSnap = budgets.Snapshot()
        match Lease.validateLeaseOp lease constraints budgetsSnap now capability target with
        | Ok () -> Task.CompletedTask
        | Error err -> raise (ArcpException err)
