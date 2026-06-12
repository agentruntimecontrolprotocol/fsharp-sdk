namespace ARCP.Runtime

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Runtime.Auth
open ARCP.Runtime.Internal

/// Per-running-job state held by `JobManager`. Carries enough to
/// drive lifecycle transitions, emit subscriber fan-out, and
/// terminate the job.
///
/// Internal: this type is not exposed on the public surface.
type internal JobRecord =
    {
        JobId: JobId
        SessionId: SessionId
        Principal: IPrincipal
        Agent: string // name@version after resolution
        Input: JsonElement
        Lease: LeaseGrant
        Constraints: LeaseConstraints option
        mutable Credentials: Credential list
        Budgets: BudgetCounters
        ParentJobId: string option
        TraceId: string option
        CreatedAt: DateTimeOffset
        Cancellation: CancellationTokenSource
        Watchdog: ExpiryWatchdog option
        /// `max_runtime_sec` watchdog (§7.1); fires TIMEOUT.
        RuntimeWatchdog: ExpiryWatchdog option
        /// Set once a terminal message has been emitted so post-terminal
        /// emissions are dropped (§7.3, §9.5).
        mutable TerminalEmitted: bool
        /// The exact `job.accepted` payload sent at acceptance, replayed
        /// verbatim for idempotent resubmits (§7.2).
        mutable AcceptedPayload: JobAcceptedPayload option
        mutable Status: JobStatus
        mutable LastEventSeq: int64
        /// Set when the agent began a streamed result via
        /// `BeginStreamingResult` (§8.4); the terminating `job.result`
        /// MUST carry this `result_id` and omit the inline result.
        mutable StreamResultId: string option
        /// Fingerprint of the original submission parameters for an
        /// idempotency-keyed job (§7.2). A replay with a different
        /// fingerprint is a conflicting reuse → `DUPLICATE_KEY`.
        IdempotencyFingerprint: string option
        /// Idempotency key claimed by this job, if any. Used to release
        /// the claim when the terminal record is evicted.
        IdempotencyKey: string option
        /// When the job reached a terminal state. Drives retention-based
        /// eviction so terminal records do not accumulate forever.
        mutable TerminatedAt: DateTimeOffset option
    }

/// Adapter that lets `JobManager` push a `job.event` (or `job.result`,
/// `job.error`) out to the right transport(s) without `JobManager`
/// knowing about transports directly. Implementations live in
/// `ArcpServer`.
type internal IJobOutbox =
    abstract member EmitJobEventAsync: record: JobRecord * body: JobEventBody -> Task
    abstract member EmitJobResultAsync: record: JobRecord * payload: JobResultPayload -> Task
    abstract member EmitJobErrorAsync: record: JobRecord * payload: JobErrorPayload -> Task
    /// Emit a `credential_rotated` status. The submitting session
    /// receives the new credential `value`; subscribers receive a
    /// redacted body (id only) per §14/§9.8.2.
    abstract member EmitCredentialRotatedAsync: record: JobRecord * credentialId: string * newValue: string -> Task

/// Tracks every running and terminated job for the runtime.
type internal JobManager(timeProvider: TimeProvider, outbox: IJobOutbox) =
    let byId = ConcurrentDictionary<string, JobRecord>()
    let idempotency = ConcurrentDictionary<string, string>()
    let subscriptions = SubscriptionFanout()

    member _.Subscriptions = subscriptions

    member _.Register(record: JobRecord) : unit = byId.[record.JobId.Value] <- record

    /// Claim the single terminal emission for `record`. Returns true for
    /// exactly one caller; subsequent callers get false and must not
    /// emit a second contradictory terminal message (§7.3, §9.5).
    member _.TryClaimTerminal(record: JobRecord) : bool =
        lock record (fun () ->
            if record.TerminalEmitted then
                false
            else
                record.TerminalEmitted <- true
                true)

    member _.TryGet(jobId: JobId) : JobRecord option =
        match byId.TryGetValue jobId.Value with
        | true, r -> Some r
        | _ -> None

    member _.AllForPrincipal(principalId: string) : JobRecord seq =
        byId
        |> Seq.filter (fun kv -> kv.Value.Principal.Id = principalId)
        |> Seq.map (fun kv -> kv.Value)

    member _.TryClaimIdempotencyKey(key: string, jobId: JobId) : Result<unit, ARCPError> =
        if idempotency.TryAdd(key, jobId.Value) then
            Ok()
        else
            match idempotency.TryGetValue key with
            | true, existing -> Error(ARCPError.DuplicateKey existing)
            | _ -> Error(ARCPError.DuplicateKey key)

    member _.LookupIdempotencyKey(key: string) : string option =
        match idempotency.TryGetValue key with
        | true, jid -> Some jid
        | _ -> None

    /// Release an idempotency key claim. Used by acceptance flows
    /// that fail after the claim succeeded — see `JobSubmitFlow`.
    member _.ReleaseIdempotencyKey(key: string, jobId: JobId) : unit =
        let kv = KeyValuePair<string, string>(key, jobId.Value)
        (idempotency :> ICollection<KeyValuePair<string, string>>).Remove(kv) |> ignore

    /// Remove a job record entirely. Used when acceptance fails
    /// after the record was registered (e.g. credential provisioner
    /// errors) so the failed job does not surface in list/get.
    member _.Unregister(jobId: JobId) : unit = byId.TryRemove(jobId.Value) |> ignore

    /// Mark `jobId` as terminated. Subsequent emit attempts on this
    /// id are dropped. Disposes the watchdog and cancellation source
    /// and clears any retained credential values (§14); the record is
    /// evicted from `byId` later by `EvictTerminated`.
    member this.Terminate(jobId: JobId, status: JobStatus) : unit =
        match this.TryGet jobId with
        | Some r ->
            r.Status <- status

            for w in [ r.Watchdog; r.RuntimeWatchdog ] do
                w
                |> Option.iter (fun w ->
                    try
                        (w :> IDisposable).Dispose()
                    with _ ->
                        ())

            try
                r.Cancellation.Cancel()
            with _ ->
                ()

            try
                r.Cancellation.Dispose()
            with _ ->
                ()

            // §14: do not retain credential secrets past termination.
            r.Credentials <- []
            r.TerminatedAt <- Some(timeProvider.GetUtcNow())
        | _ -> ()

    /// Evict terminal records whose termination is older than
    /// `retentionCutoff`, releasing their idempotency-key claims. Keeps
    /// `byId` bounded under sustained churn while leaving recent
    /// terminals visible to `list_jobs`/replay.
    member _.EvictTerminated(retentionCutoff: DateTimeOffset) : int =
        let mutable removed = 0

        for kv in byId do
            match kv.Value.TerminatedAt with
            | Some t when t < retentionCutoff ->
                if byId.TryRemove kv.Key |> fst then
                    removed <- removed + 1

                    kv.Value.IdempotencyKey
                    |> Option.iter (fun key ->
                        let pair = KeyValuePair<string, string>(key, kv.Key)
                        (idempotency :> ICollection<KeyValuePair<string, string>>).Remove pair |> ignore)
            | _ -> ()

        removed

    /// Snapshot the record into a `JobSummary` shape for §6.6
    /// `session.list_jobs` responses.
    member _.ToSummary(r: JobRecord) : JobSummary =
        {
            JobId = r.JobId.Value
            Agent = r.Agent
            Status = r.Status
            Lease = r.Lease
            ParentJobId = r.ParentJobId
            CreatedAt = r.CreatedAt
            TraceId = r.TraceId
            LastEventSeq = r.LastEventSeq
        }

    /// Emit a `job.event` for `record` via the configured `IJobOutbox`.
    /// The outbox is responsible for updating `LastEventSeq`.
    member this.EmitEventAsync(record: JobRecord, body: JobEventBody) : Task = outbox.EmitJobEventAsync(record, body)

    member this.EmitResultAsync(record: JobRecord, payload: JobResultPayload) : Task =
        outbox.EmitJobResultAsync(record, payload)

    member this.EmitErrorAsync(record: JobRecord, payload: JobErrorPayload) : Task =
        outbox.EmitJobErrorAsync(record, payload)

    member this.EmitCredentialRotatedAsync(record: JobRecord, credentialId: string, newValue: string) : Task =
        outbox.EmitCredentialRotatedAsync(record, credentialId, newValue)
