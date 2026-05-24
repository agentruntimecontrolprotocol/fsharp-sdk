module ARCP.UnitTests.JobEventBodyTests

open Xunit
open FsUnit.Xunit
open ARCP.Core

let private el = Json.serializeToElement<int> 0

[<Fact>]
let ``kind returns reserved wire string for each case`` () =
    JobEventBody.kind (JobEventBody.Log(LogLevel.Info, "")) |> should equal "log"
    JobEventBody.kind (JobEventBody.Thought "x") |> should equal "thought"
    JobEventBody.kind (JobEventBody.ToolCall("t", el, "c")) |> should equal "tool_call"
    JobEventBody.kind (JobEventBody.ToolResult("c", ToolOutcome.Result el)) |> should equal "tool_result"
    JobEventBody.kind (JobEventBody.Status("phase", None)) |> should equal "status"
    JobEventBody.kind (JobEventBody.Metric("m", 1m, None, None)) |> should equal "metric"
    JobEventBody.kind (JobEventBody.ArtifactRef("u", "ct", None, None)) |> should equal "artifact_ref"

    JobEventBody.kind (
        JobEventBody.Delegate
            {
                ChildJobId = "j"
                Agent = "a"
                Lease = Lease.empty
                LeaseConstraints = None
            }
    )
    |> should equal "delegate"

    JobEventBody.kind (JobEventBody.Progress(1m, None, None, None)) |> should equal "progress"

    JobEventBody.kind (JobEventBody.ResultChunk("r", 0L, "d", ChunkEncoding.Utf8, false))
    |> should equal "result_chunk"

[<Fact>]
let ``XVendor preserves vendor kind verbatim`` () =
    JobEventBody.kind (JobEventBody.XVendor("x-vendor.foo", el))
    |> should equal "x-vendor.foo"

[<Fact>]
let ``JobStatus.tryOfWire round-trips canonical wire strings`` () =
    [ JobStatus.Pending
      JobStatus.Running
      JobStatus.Success
      JobStatus.Error
      JobStatus.Cancelled
      JobStatus.TimedOut ]
    |> List.iter (fun s ->
        match JobStatus.tryOfWire (JobStatus.toWire s) with
        | Ok back -> back |> should equal s
        | Error e -> failwithf "round-trip failed for %A: %s" s e)

[<Fact>]
let ``JobStatus.tryOfWire rejects unknown strings`` () =
    (JobStatus.tryOfWire "bogus").IsError |> should equal true

[<Fact>]
let ``JobStatus.ofWire throws on unknown`` () =
    Assert.Throws<System.ArgumentException>(fun () -> JobStatus.ofWire "nope" |> ignore)
    |> ignore

[<Fact>]
let ``AuthPayload.ofScheme maps schemes to wire payload`` () =
    let b = AuthPayload.ofScheme (AuthScheme.Bearer "tok")
    b.Scheme |> should equal "bearer"
    b.Token |> should equal (Some "tok")
    let n = AuthPayload.ofScheme AuthScheme.None
    n.Scheme |> should equal "none"
    n.Token |> should equal None
