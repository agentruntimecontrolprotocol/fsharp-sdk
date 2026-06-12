namespace ARCP.Client.Transport

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client

/// In-process loopback transport. A pair of channels carries
/// envelopes between two endpoints — typically a client and a
/// runtime running in the same process for tests and samples.
type MemoryTransport private (outgoing: Channel<Envelope>, incoming: Channel<Envelope>) =
    interface ITransport with
        member _.SendAsync(env, ct) =
            task { do! outgoing.Writer.WriteAsync(env, ct).AsTask() } :> Task

        member _.Receive(ct) =
            let reader = incoming.Reader

            { new IAsyncEnumerable<Envelope> with
                member _.GetAsyncEnumerator(c) =
                    let linked = CancellationTokenSource.CreateLinkedTokenSource(c, ct)
                    let mutable current = Unchecked.defaultof<Envelope>

                    { new IAsyncEnumerator<Envelope> with
                        member _.Current = current

                        member _.MoveNextAsync() =
                            task {
                                try
                                    let! has = reader.WaitToReadAsync(linked.Token).AsTask()

                                    if not has then
                                        return false
                                    else
                                        let success, e = reader.TryRead()

                                        if success then
                                            current <- e
                                            return true
                                        else
                                            return false
                                with :? OperationCanceledException ->
                                    return false
                            }
                            |> ValueTask<bool>

                        member _.DisposeAsync() =
                            linked.Dispose()
                            ValueTask.CompletedTask
                    }
            }

        member _.CloseAsync(_) =
            outgoing.Writer.TryComplete() |> ignore
            incoming.Writer.TryComplete() |> ignore
            Task.CompletedTask

    /// Create a linked client/server transport pair. Anything sent
    /// on the client is received by the server, and vice versa.
    static member CreatePair() : ITransport * ITransport =
        let a = Channel.CreateUnbounded<Envelope>()
        let b = Channel.CreateUnbounded<Envelope>()
        let client = new MemoryTransport(a, b) :> ITransport
        let server = new MemoryTransport(b, a) :> ITransport
        client, server
