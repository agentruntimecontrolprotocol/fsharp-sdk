namespace ARCP.Transport

open System
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open ARCP.Envelope
open ARCP.Messages.Registry

/// <summary>
/// In-process paired transport useful for tests and same-host runtimes.
/// Each call to <see cref="Memory.createPair"/> returns two endpoints
/// whose <c>Send</c> on one side becomes <c>Receive</c> on the other.
/// </summary>
module Memory =

    type private ChannelTransport
        (reader: ChannelReader<Envelope<MessageType>>, writer: ChannelWriter<Envelope<MessageType>>) =
        interface ITransport with
            member _.SendAsync(envelope, ct) =
                task {
                    let! _ = writer.WriteAsync(envelope, ct)
                    return ()
                }

            member _.ReceiveAsync(ct) =
                task {
                    try
                        let! has = reader.WaitToReadAsync(ct)

                        if has then
                            match reader.TryRead() with
                            | true, env -> return Some env
                            | _ -> return None
                        else
                            return None
                    with :? ChannelClosedException ->
                        return None
                }

            member _.DisposeAsync() =
                ValueTask(task { writer.TryComplete() |> ignore })

    /// <summary>
    /// Create two paired in-memory transports. Their channels are
    /// unbounded and FIFO; suitable for tests but not for production
    /// throughput tuning.
    /// </summary>
    let createPair () : ITransport * ITransport =
        let aToB = Channel.CreateUnbounded<Envelope<MessageType>>()
        let bToA = Channel.CreateUnbounded<Envelope<MessageType>>()
        let a = new ChannelTransport(bToA.Reader, aToB.Writer) :> ITransport
        let b = new ChannelTransport(aToB.Reader, bToA.Writer) :> ITransport
        a, b
