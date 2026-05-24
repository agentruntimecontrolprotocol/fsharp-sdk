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
        mutable Status: JobStatus
        mutable LastEventSeq: int64
    }

/// Adapter that lets `JobManager` push a `job.event` (or `job.result`,
/// `job.error`) out to the right transport(s) without `JobManager`
/// knowing about transports directly. Implementations live in
/// `ArcpServer`.
type internal IJobOutbox =
    abstract member EmitJobEventAsync: record: JobRecord * body: JobEventBody -> Task
    abstract member EmitJobResultAsync: record: JobRecord * payload: JobResultPayload -> Task
    abstract member EmitJobErrorAsync: record: JobRecord * payload: JobErrorPayload -> Task

/// Tracks every running and terminated job for the runtime.
type internal JobManager(timeProvider: TimeProvider, outbox: IJobOutbox) =
    let byId = ConcurrentDictionary<string, JobRecord>()
    let idempotency = ConcurrentDictionary<string, string>()
    let subscriptions = SubscriptionFanout()

    member _.Subscriptions = subscriptions

    member _.Register(record: JobRecord) : unit = byId.[record.JobId.Value] <- record

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
    /// id are dropped.
    member this.Terminate(jobId: JobId, status: JobStatus) : unit =
        match this.TryGet jobId with
        | Some r ->
            r.Status <- status
            r.Watchdog |> Option.iter (fun w -> w.Stop())

            try
                r.Cancellation.Cancel()
            with _ ->
                ()
        | _ -> ()

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

    /// Emit a `job.event` for `record`. Updates `LastEventSeq`.
    member this.EmitEventAsync(record: JobRecord, body: JobEventBody) : Task = outbox.EmitJobEventAsync(record, body)

    member this.EmitResultAsync(record: JobRecord, payload: JobResultPayload) : Task =
        outbox.EmitJobResultAsync(record, payload)

    member this.EmitErrorAsync(record: JobRecord, payload: JobErrorPayload) : Task =
        outbox.EmitJobErrorAsync(record, payload)
