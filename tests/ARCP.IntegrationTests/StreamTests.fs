module ARCP.IntegrationTests.StreamTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharp.Control
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Streaming
open ARCP.Messages.Registry
open ARCP.Runtime

let private jsonInt (n: int) : JsonElement =
    JsonSerializer.SerializeToElement<int>(n)

[<Fact>]
let ``open stream, writer writes 3 chunks, reader receives in order`` () =
    task {
        let captured =
            System.Collections.Concurrent.ConcurrentQueue<Envelope<MessageType>>()

        let send env =
            captured.Enqueue env
            Task.CompletedTask

        let mgr = StreamManager(send, capacity = 8)

        let! writer = mgr.OpenWriterAsync(StreamKind.Text, None)

        // Register an incoming side mirroring writer.StreamId
        let reader = mgr.RegisterIncoming(writer.StreamId, StreamKind.Text)

        // Pump captured stream.chunk envelopes back through HandleAsync.
        let pumpDone = TaskCompletionSource<unit>()

        let pump =
            task {
                while not pumpDone.Task.IsCompleted do
                    let mutable e = Unchecked.defaultof<_>

                    if captured.TryDequeue(&e) then
                        do! mgr.HandleAsync e
                    else
                        do! Task.Delay(5)
            }
            :> Task

        let _ = pump

        let! _ = writer.WriteChunkAsync(jsonInt 1)
        let! _ = writer.WriteChunkAsync(jsonInt 2)
        let! _ = writer.WriteChunkAsync(jsonInt 3)
        do! writer.CloseAsync()

        // give the pump a moment
        do! Task.Delay(100)
        pumpDone.TrySetResult() |> ignore

        let collected = System.Collections.Generic.List<int>()

        do!
            reader.ReadAllAsync(CancellationToken.None)
            |> TaskSeq.iter (fun c -> collected.Add(c.Data.GetInt32()))

        Assert.Equal<int list>([ 1; 2; 3 ], List.ofSeq collected)
    }

[<Fact>]
let ``out-of-order chunk surfaces as exception in reader enumerable`` () =
    task {
        let captured =
            System.Collections.Concurrent.ConcurrentQueue<Envelope<MessageType>>()

        let send env =
            captured.Enqueue env
            Task.CompletedTask

        let mgr = StreamManager(send, capacity = 8)

        let sid = StreamId.create ()
        let reader = mgr.RegisterIncoming(sid, StreamKind.Text)

        // Manually inject an out-of-order chunk envelope (sequence=2 first)
        let badChunk: StreamChunk =
            {
                Sequence = 2
                Data = jsonInt 99
                Sha256 = None
            }

        let env = Envelopes.streamChunk badChunk |> Envelope.withStream sid

        do! mgr.HandleAsync env

        let mutable raised = false

        try
            do! reader.ReadAllAsync(CancellationToken.None) |> TaskSeq.iter (fun _ -> ())
        with _ ->
            raised <- true

        Assert.True(raised, "expected reader to throw on out-of-order chunk")
    }
