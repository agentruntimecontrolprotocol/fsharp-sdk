module ARCP.UnitTests.JobHandleTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Client
open ARCP.Client.Internal

let private mkHandleNoCancel (jobId: JobId) =
    mkHandle jobId [] (fun _ -> Task.FromResult(Ok()))

[<Fact>]
let ``Events stream emits each enqueued body in order`` () =
    let handle, writer = mkHandleNoCancel (JobId.ofString "j-1")
    writer.Channel.Writer.TryWrite(JobEventBody.Log(LogLevel.Info, "a")) |> ignore
    writer.Channel.Writer.TryWrite(JobEventBody.Thought "b") |> ignore
    writer.Channel.Writer.TryComplete() |> ignore

    let events = ResizeArray<JobEventBody>()
    let enumerator = handle.Events.GetAsyncEnumerator(CancellationToken.None)

    try
        let mutable more = true

        while more do
            let next = enumerator.MoveNextAsync().AsTask()

            if next.Result then
                events.Add enumerator.Current
            else
                more <- false
    finally
        ignore (enumerator.DisposeAsync().AsTask())

    events.Count |> should equal 2
    JobEventBody.kind events.[0] |> should equal "log"
    JobEventBody.kind events.[1] |> should equal "thought"

[<Fact>]
let ``Result resolves to Ok payload once ResultSetter fires`` () =
    let handle, writer = mkHandleNoCancel (JobId.ofString "j-1")

    let payload: JobResultPayload =
        {
            FinalStatus = JobStatus.Success
            Result = Some(Json.serializeToElement<int> 42)
            ResultId = None
            ResultSize = None
            Summary = None
        }

    writer.ResultSetter.TrySetResult(Ok payload) |> ignore

    match handle.Result.Result with
    | Ok r -> r.FinalStatus |> should equal JobStatus.Success
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``Result resolves to Error on ResultSetter Error`` () =
    let handle, writer = mkHandleNoCancel (JobId.ofString "j-1")
    writer.ResultSetter.TrySetResult(Error(ARCPError.JobNotFound "j-1")) |> ignore

    match handle.Result.Result with
    | Error(ARCPError.JobNotFound _) -> ()
    | other -> failwithf "expected JobNotFound, got %A" other

[<Fact>]
let ``CancelAsync invokes the cancel delegate`` () =
    let invocations = ref 0
    let jobId = JobId.ofString "j-1"

    let handle, _ =
        mkHandle jobId [] (fun _ ->
            Interlocked.Increment(invocations) |> ignore
            Task.FromResult(Ok()))

    let r = (handle.CancelAsync(Some "user", CancellationToken.None)).Result
    r.IsOk |> should equal true
    invocations.Value |> should equal 1

[<Fact>]
let ``TryReadResultBytes returns None when no chunks have arrived`` () =
    let handle, _ = mkHandleNoCancel (JobId.ofString "j-1")
    handle.TryReadResultBytes(ResultId.newId ()) |> should equal None

[<Fact>]
let ``TryReadResultBytes returns the assembled bytes once the stream closes`` () =
    let handle, writer = mkHandleNoCancel (JobId.ofString "j-1")
    let rid = ResultId.newId ()
    let asm = writer.ChunkIndex.GetOrCreate rid.Value
    asm.Append(0L, "abc", ChunkEncoding.Utf8, false) |> ignore

    match handle.TryReadResultBytes rid with
    | Some bytes -> System.Text.Encoding.UTF8.GetString bytes |> should equal "abc"
    | None -> failwith "expected Some bytes"

[<Fact>]
let ``Credentials exposed on the handle round-trip`` () =
    let creds =
        [
            {
                Id = "c-1"
                Scheme = "bearer"
                Value = "secret"
                Endpoint = "https://api"
                Profile = None
                Constraints = None
            }
        ]

    let handle, _ =
        mkHandle (JobId.ofString "j-1") creds (fun _ -> Task.FromResult(Ok()))

    handle.Credentials |> should equal creds
