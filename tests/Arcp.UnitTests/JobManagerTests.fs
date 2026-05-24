module ARCP.UnitTests.JobManagerTests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Time.Testing
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime
open ARCP.Runtime.Auth
open ARCP.Runtime.Internal

let private noopOutbox =
    { new IJobOutbox with
        member _.EmitJobEventAsync(_, _) = Task.CompletedTask
        member _.EmitJobResultAsync(_, _) = Task.CompletedTask
        member _.EmitJobErrorAsync(_, _) = Task.CompletedTask
    }

let private mkRecord (jobId: JobId) (principalId: string) : JobRecord =
    {
        JobId = jobId
        SessionId = SessionId.newId ()
        Principal = StringPrincipal principalId :> IPrincipal
        Agent = "echo@default"
        Input = Json.serializeToElement<int> 0
        Lease = Lease.empty
        Constraints = None
        Credentials = []
        Budgets = BudgetCounters()
        ParentJobId = None
        TraceId = None
        CreatedAt = DateTimeOffset.UnixEpoch
        Cancellation = new CancellationTokenSource()
        Watchdog = None
        Status = JobStatus.Pending
        LastEventSeq = 0L
    }

[<Fact>]
let ``TryClaimIdempotencyKey rejects a second claim with DuplicateKey`` () =
    let jm = JobManager(FakeTimeProvider(), noopOutbox)
    let jid1 = JobId.newId ()
    let jid2 = JobId.newId ()
    (jm.TryClaimIdempotencyKey("k", jid1)).IsOk |> should equal true

    match jm.TryClaimIdempotencyKey("k", jid2) with
    | Error(ARCPError.DuplicateKey _) -> ()
    | other -> failwithf "expected DuplicateKey, got %A" other

[<Fact>]
let ``LookupIdempotencyKey returns the claimed job id`` () =
    let jm = JobManager(FakeTimeProvider(), noopOutbox)
    let jid = JobId.newId ()
    jm.TryClaimIdempotencyKey("k", jid) |> ignore
    jm.LookupIdempotencyKey "k" |> should equal (Some jid.Value)
    jm.LookupIdempotencyKey "missing" |> should equal None

[<Fact>]
let ``ReleaseIdempotencyKey frees the key for re-use`` () =
    let jm = JobManager(FakeTimeProvider(), noopOutbox)
    let jid = JobId.newId ()
    jm.TryClaimIdempotencyKey("k", jid) |> ignore
    jm.ReleaseIdempotencyKey("k", jid)
    jm.LookupIdempotencyKey "k" |> should equal None
    // Re-claim works.
    let jid2 = JobId.newId ()
    (jm.TryClaimIdempotencyKey("k", jid2)).IsOk |> should equal true

[<Fact>]
let ``Register then TryGet round-trips records`` () =
    let jm = JobManager(FakeTimeProvider(), noopOutbox)
    let jid = JobId.newId ()
    let record = mkRecord jid "u-1"
    jm.Register record

    match jm.TryGet jid with
    | Some r -> r.JobId |> should equal jid
    | None -> failwith "expected Some"

[<Fact>]
let ``TryGet returns None for unknown ids`` () =
    let jm = JobManager(FakeTimeProvider(), noopOutbox)
    jm.TryGet(JobId.newId ()) |> should equal None

[<Fact>]
let ``Unregister removes the record`` () =
    let jm = JobManager(FakeTimeProvider(), noopOutbox)
    let jid = JobId.newId ()
    jm.Register(mkRecord jid "u-1")
    jm.Unregister jid
    jm.TryGet jid |> should equal None

[<Fact>]
let ``AllForPrincipal filters by principal id`` () =
    let jm = JobManager(FakeTimeProvider(), noopOutbox)
    jm.Register(mkRecord (JobId.newId ()) "alice")
    jm.Register(mkRecord (JobId.newId ()) "alice")
    jm.Register(mkRecord (JobId.newId ()) "bob")
    jm.AllForPrincipal "alice" |> Seq.length |> should equal 2
    jm.AllForPrincipal "bob" |> Seq.length |> should equal 1
    jm.AllForPrincipal "carol" |> Seq.length |> should equal 0

[<Fact>]
let ``Terminate updates status and stops cancellation`` () =
    let jm = JobManager(FakeTimeProvider(), noopOutbox)
    let jid = JobId.newId ()
    let record = mkRecord jid "u-1"
    jm.Register record
    jm.Terminate(jid, JobStatus.Success)

    match jm.TryGet jid with
    | Some r ->
        r.Status |> should equal JobStatus.Success
        r.Cancellation.IsCancellationRequested |> should equal true
    | None -> failwith "expected Some"

[<Fact>]
let ``ToSummary projects the record into a JobSummary`` () =
    let jm = JobManager(FakeTimeProvider(), noopOutbox)
    let jid = JobId.newId ()
    let record = mkRecord jid "u-1"
    let summary = jm.ToSummary record
    summary.JobId |> should equal jid.Value
    summary.Agent |> should equal "echo@default"
    summary.Status |> should equal JobStatus.Pending
