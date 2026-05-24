module ARCP.UnitTests.JobContextTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Time.Testing
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime
open ARCP.Runtime.Internal

let private mkContext (lease: LeaseGrant) =
    let budgets = BudgetCounters()
    budgets.SetInitial(Lease.initialBudgets lease)
    let emitted = ResizeArray<JobEventBody>()
    let rotated = ResizeArray<string * string>()
    let costs = ResizeArray<string * decimal>()
    let chunkIds = ResizeArray<ResultId>()

    let ctx =
        JobContext(
            JobId.ofString "j-1",
            SessionId.ofString "s-1",
            None,
            Json.serializeToElement<int> 1,
            lease,
            None,
            [],
            budgets,
            FakeTimeProvider(),
            CancellationToken.None,
            (fun body ->
                emitted.Add body
                Task.CompletedTask),
            (fun (id, value, _) ->
                rotated.Add(id, value)
                Task.CompletedTask),
            (fun () ->
                let id = ResultId.newId ()
                chunkIds.Add id
                id),
            (fun (currency, amount) -> costs.Add(currency, amount))
        )

    ctx, emitted, rotated, costs, budgets

[<Fact>]
let ``EmitLog records a Log event`` () =
    let ctx, emitted, _, _, _ = mkContext Lease.empty
    ctx.EmitLogAsync(LogLevel.Info, "hello", CancellationToken.None).Wait()
    emitted.Count |> should equal 1
    JobEventBody.kind emitted.[0] |> should equal "log"

[<Fact>]
let ``EmitThought records a Thought event`` () =
    let ctx, emitted, _, _, _ = mkContext Lease.empty
    ctx.EmitThoughtAsync("idea", CancellationToken.None).Wait()
    JobEventBody.kind emitted.[0] |> should equal "thought"

[<Fact>]
let ``EmitToolCall and EmitToolResult emit matching events`` () =
    let ctx, emitted, _, _, _ = mkContext Lease.empty
    ctx.EmitToolCallAsync("search", Json.serializeToElement "q", "c-1", CancellationToken.None).Wait()

    ctx.EmitToolResultAsync("c-1", ToolOutcome.Result(Json.serializeToElement "ok"), CancellationToken.None)
        .Wait()

    [ "tool_call"; "tool_result" ]
    |> List.iteri (fun i k -> JobEventBody.kind emitted.[i] |> should equal k)

[<Fact>]
let ``EmitStatus emits a Status event with message`` () =
    let ctx, emitted, _, _, _ = mkContext Lease.empty
    ctx.EmitStatusAsync("starting", Some "warming up", CancellationToken.None).Wait()
    JobEventBody.kind emitted.[0] |> should equal "status"

[<Fact>]
let ``EmitProgress emits a Progress event`` () =
    let ctx, emitted, _, _, _ = mkContext Lease.empty
    ctx.EmitProgressAsync(1m, Some 10m, Some "pages", Some "scanning", CancellationToken.None)
        .Wait()

    JobEventBody.kind emitted.[0] |> should equal "progress"

[<Fact>]
let ``EmitMetric with positive cost.* decrements budget tracking`` () =
    let ctx, _, _, costs, _ = mkContext Lease.empty
    ctx.EmitMetricAsync("cost.openai", 0.25m, Some "USD", None, CancellationToken.None).Wait()
    costs.Count |> should equal 1
    costs.[0] |> should equal ("USD", 0.25m)

[<Fact>]
let ``EmitMetric with negative value is silently dropped`` () =
    let ctx, emitted, _, costs, _ = mkContext Lease.empty
    ctx.EmitMetricAsync("anything", -1m, None, None, CancellationToken.None).Wait()
    emitted.Count |> should equal 0
    costs.Count |> should equal 0

[<Fact>]
let ``EmitMetric non-cost name does not touch budget`` () =
    let ctx, emitted, _, costs, _ = mkContext Lease.empty
    ctx.EmitMetricAsync("latency.ms", 12m, Some "ms", None, CancellationToken.None).Wait()
    emitted.Count |> should equal 1
    costs.Count |> should equal 0

[<Fact>]
let ``EmitArtifactRef emits an ArtifactRef event`` () =
    let ctx, emitted, _, _, _ = mkContext Lease.empty

    ctx.EmitArtifactRefAsync("file://x", "text/plain", Some 10L, Some "deadbeef", CancellationToken.None)
        .Wait()

    JobEventBody.kind emitted.[0] |> should equal "artifact_ref"

[<Fact>]
let ``EmitVendorEvent accepts x-vendor.* kinds`` () =
    let ctx, emitted, _, _, _ = mkContext Lease.empty
    ctx.EmitVendorEventAsync("x-vendor.foo", Json.serializeToElement "x", CancellationToken.None)
        .Wait()

    JobEventBody.kind emitted.[0] |> should equal "x-vendor.foo"

[<Fact>]
let ``EmitVendorEvent rejects non-x-vendor kinds`` () =
    let ctx, _, _, _, _ = mkContext Lease.empty

    let ex =
        Assert.Throws<System.ArgumentException>(fun () ->
            ctx.EmitVendorEventAsync("oops", Json.serializeToElement "x", CancellationToken.None)
                .Wait())

    ex.Message |> should haveSubstring "x-vendor."

[<Fact>]
let ``BeginStreamingResult mints a result id`` () =
    let ctx, _, _, _, _ = mkContext Lease.empty
    let id = ctx.BeginStreamingResult()
    id.Value |> should not' (be NullOrEmptyString)

[<Fact>]
let ``EmitResultChunk utf8 encodes the byte span as text`` () =
    let ctx, emitted, _, _, _ = mkContext Lease.empty
    let rid = ResultId.newId ()
    let payload = System.Text.Encoding.UTF8.GetBytes "abc"

    ctx.EmitResultChunkAsync(rid, 0L, ReadOnlyMemory(payload), ChunkEncoding.Utf8, false, CancellationToken.None)
        .Wait()

    match emitted.[0] with
    | JobEventBody.ResultChunk(_, _, data, enc, more) ->
        data |> should equal "abc"
        enc |> should equal ChunkEncoding.Utf8
        more |> should equal false
    | other -> failwithf "expected ResultChunk, got %A" other

[<Fact>]
let ``EmitResultChunk base64 encodes the byte span`` () =
    let ctx, emitted, _, _, _ = mkContext Lease.empty
    let rid = ResultId.newId ()
    let payload = [| 1uy; 2uy; 3uy |]

    ctx.EmitResultChunkAsync(rid, 0L, ReadOnlyMemory(payload), ChunkEncoding.Base64, true, CancellationToken.None)
        .Wait()

    match emitted.[0] with
    | JobEventBody.ResultChunk(_, _, data, ChunkEncoding.Base64, true) ->
        data |> should equal (Convert.ToBase64String payload)
    | other -> failwithf "expected base64 ResultChunk, got %A" other

[<Fact>]
let ``ValidateOpAsync returns CompletedTask when lease grants the operation`` () =
    let lease = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/a/**" ]
    let ctx, _, _, _, _ = mkContext lease
    ctx.ValidateOpAsync(Capabilities.FsRead, "/a/file", CancellationToken.None).Wait()

[<Fact>]
let ``ValidateOpAsync throws ArcpException for denied ops`` () =
    let lease = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/a/**" ]
    let ctx, _, _, _, _ = mkContext lease

    let ex =
        Assert.Throws<ArcpException>(fun () ->
            ctx.ValidateOpAsync(Capabilities.FsWrite, "/a/file", CancellationToken.None).Wait())

    ARCPError.code ex.Error |> should equal "PERMISSION_DENIED"

[<Fact>]
let ``RotateCredentialAsync invokes the rotate callback`` () =
    let ctx, _, rotated, _, _ = mkContext Lease.empty
    ctx.RotateCredentialAsync("c-1", "v-2", CancellationToken.None).Wait()
    rotated.Count |> should equal 1
    rotated.[0] |> should equal ("c-1", "v-2")

[<Fact>]
let ``EmitDelegate emits a Delegate event`` () =
    let ctx, emitted, _, _, _ = mkContext Lease.empty

    let body: DelegateBody =
        {
            ChildJobId = "j-2"
            Agent = "a"
            Lease = Lease.empty
            LeaseConstraints = None
        }

    ctx.EmitDelegateAsync(body, CancellationToken.None).Wait()
    JobEventBody.kind emitted.[0] |> should equal "delegate"

[<Fact>]
let ``JobContext exposes RemainingBudget snapshot`` () =
    let lease =
        Lease.empty |> Lease.withCapability Capabilities.CostBudget [ "USD:5.00" ]

    let ctx, _, _, _, _ = mkContext lease
    ctx.RemainingBudget |> Map.find "USD" |> should equal 5m
