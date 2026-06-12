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
type JobContext
    internal
    (
        jobId: JobId,
        sessionId: SessionId,
        parentJobId: JobId option,
        input: JsonElement,
        lease: LeaseGrant,
        constraints: LeaseConstraints option,
        credentials: Credential list,
        budgets: BudgetCounters,
        timeProvider: TimeProvider,
        cancellationToken: CancellationToken,
        emit: JobEventBody -> Task,
        rotateCredential: string * string * CancellationToken -> Task,
        streamResultBegin: unit -> ResultId,
        onCostMetric: string * decimal -> unit
    ) =
    // Per-result_id chunk ordering state (§8.4): next expected chunk_seq
    // and whether the stream was closed by a `more=false` chunk.
    let chunkNext = System.Collections.Generic.Dictionary<string, int64>()
    let chunkClosed = System.Collections.Generic.HashSet<string>()
    let chunkLock = obj ()

    member _.JobId: JobId = jobId
    member _.SessionId: SessionId = sessionId
    member _.ParentJobId: JobId option = parentJobId
    member _.Input: JsonElement = input
    member _.Lease: LeaseGrant = lease
    member _.LeaseConstraints: LeaseConstraints option = constraints
    member _.Credentials: Credential list = credentials
    member _.CancellationToken: CancellationToken = cancellationToken

    /// Snapshot of remaining budget counters.
    member _.RemainingBudget: Map<string, decimal> = budgets.Snapshot()

    member _.EmitLogAsync(level: LogLevel, message: string, _ct: CancellationToken) : Task =
        emit (JobEventBody.Log(level, message))

    member _.EmitThoughtAsync(text: string, _ct: CancellationToken) : Task = emit (JobEventBody.Thought text)

    member _.EmitToolCallAsync(tool: string, args: JsonElement, callId: string, _ct: CancellationToken) : Task =
        emit (JobEventBody.ToolCall(tool, args, callId))

    member _.EmitToolResultAsync(callId: string, outcome: ToolOutcome, _ct: CancellationToken) : Task =
        emit (JobEventBody.ToolResult(callId, outcome))

    member _.EmitStatusAsync(phase: string, message: string option, _ct: CancellationToken) : Task =
        emit (JobEventBody.Status(phase, message))

    /// Emit a credential-rotation status event and revoke the prior
    /// credential id through the runtime registry.
    member _.RotateCredentialAsync(credentialId: string, newValue: string, ct: CancellationToken) : Task =
        rotateCredential (credentialId, newValue, ct)

    /// Emit a `progress` event (§8.2.1). `current` MUST be non-negative
    /// (rejected with INVALID_REQUEST otherwise); when `total` is present
    /// `current` is clamped to `total` so the wire invariant holds.
    member _.EmitProgressAsync
        (current: decimal, total: decimal option, units: string option, message: string option, _ct: CancellationToken)
        : Task =
        if current < 0m then
            raise (ArcpException(ARCPError.InvalidRequest("progress.current must be non-negative", None)))

        let clamped =
            match total with
            | Some t when current > t -> t
            | _ -> current

        emit (JobEventBody.Progress(clamped, total, units, message))

    /// Emit a `metric` event. Names starting with `cost.` and a
    /// budgeted `unit` decrement the matching budget counter
    /// (spec §9.6). Negative values are rejected per §9.6.
    member _.EmitMetricAsync
        (
            name: string,
            value: decimal,
            unit: string option,
            dimensions: Map<string, string> option,
            _ct: CancellationToken
        ) : Task =
        if value < 0m then
            // §9.6: negative cost metrics are rejected; other negative
            // metrics are not governed by §9.6 and still flow through.
            if name.StartsWith("cost.") then
                raise (ArcpException(ARCPError.InvalidRequest("cost metric value must be non-negative", None)))
            else
                emit (JobEventBody.Metric(name, value, unit, dimensions))
        else
            // §9.6: `cost.budget.*` is budget telemetry (e.g.
            // `cost.budget.remaining`), not a charge — it must not
            // decrement the counter. Only genuine `cost.*` spend metrics do.
            if name.StartsWith("cost.") && not (name.StartsWith("cost.budget.")) then
                match unit with
                | Some u -> onCostMetric (u, value)
                | None -> ()

            emit (JobEventBody.Metric(name, value, unit, dimensions))

    member _.EmitArtifactRefAsync
        (uri: string, contentType: string, byteSize: int64 option, sha256: string option, _ct: CancellationToken)
        : Task =
        emit (JobEventBody.ArtifactRef(uri, contentType, byteSize, sha256))

    /// Emit a `delegate` event after validating that the child lease is
    /// a strict subset of this job's lease (spec §9.4). A child lease
    /// that names an uncovered capability, exceeds the parent's
    /// remaining budget, or extends `expires_at` beyond the parent's is
    /// rejected with `LEASE_SUBSET_VIOLATION` before any event is emitted.
    member _.EmitDelegateAsync(body: DelegateBody, _ct: CancellationToken) : Task =
        match
            Lease.isSubset
                body.Lease
                lease
                (budgets.Snapshot())
                (constraints |> Option.map (fun c -> c.ExpiresAt))
                (body.LeaseConstraints |> Option.map (fun c -> c.ExpiresAt))
        with
        | Ok() -> emit (JobEventBody.Delegate body)
        | Error err -> raise (ArcpException err)

    member _.EmitVendorEventAsync(kind: string, body: JsonElement, _ct: CancellationToken) : Task =
        if not (kind.StartsWith("x-vendor.", StringComparison.Ordinal)) then
            invalidArg "kind" "Vendor event kinds must start with x-vendor."

        emit (JobEventBody.XVendor(kind, body))

    /// Begin streaming a chunked result. Returns the `result_id`
    /// the runtime generated; the agent then calls `EmitResultChunk`
    /// repeatedly until `more = false`. The terminating `job.result`
    /// MUST carry this `result_id`.
    member _.BeginStreamingResult() : ResultId = streamResultBegin ()

    member _.EmitResultChunkAsync
        (
            resultId: ResultId,
            chunkSeq: int64,
            data: ReadOnlyMemory<byte>,
            encoding: ChunkEncoding,
            more: bool,
            _ct: CancellationToken
        ) : Task =
        // §8.4: chunk_seq is 0-based monotonic per result_id and chunks
        // MUST be emitted in order; nothing may follow a `more=false`
        // chunk. Enforce both before anything reaches the wire.
        lock chunkLock (fun () ->
            if chunkClosed.Contains resultId.Value then
                raise (
                    ArcpException(
                        ARCPError.InternalError(
                            sprintf "result_id %s already completed; no further chunks allowed" resultId.Value
                        )
                    )
                )

            let expected =
                match chunkNext.TryGetValue resultId.Value with
                | true, n -> n
                | _ -> 0L

            if chunkSeq <> expected then
                raise (
                    ArcpException(
                        ARCPError.InternalError(
                            sprintf
                                "out-of-order chunk_seq %d (expected %d) for result_id %s"
                                chunkSeq
                                expected
                                resultId.Value
                        )
                    )
                )

            chunkNext.[resultId.Value] <- expected + 1L

            if not more then
                chunkClosed.Add resultId.Value |> ignore)

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
        | Ok() -> Task.CompletedTask
        | Error err -> raise (ArcpException err)

/// Function the agent registers (spec §7.1). Receives a `JobContext`
/// and returns the agent's inline result. If the agent streamed via
/// `result_chunk`, the returned value is ignored.
type ArcpAgentHandler = JobContext -> Task<JsonElement>
