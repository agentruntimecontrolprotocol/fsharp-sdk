namespace ARCP.Client.Transport

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client

/// Newline-delimited JSON over a pair of streams. Used for stdio
/// child processes (spec §4: stdio mandatory for in-process children).
type StdioTransport(input: TextReader, output: TextWriter, ownsStreams: bool) =
    let mutable closed = false
    let writeLock = obj ()

    interface ITransport with
        member _.SendAsync(env, _ct) =
            task {
                let json = Codec.writeEnvelope env

                lock writeLock (fun () ->
                    output.Write json
                    output.Write '\n'
                    output.Flush())

                return ()
            }
            :> Task

        member _.Receive(ct) =
            { new IAsyncEnumerable<Envelope> with
                member _.GetAsyncEnumerator(c) =
                    let linked = CancellationTokenSource.CreateLinkedTokenSource(c, ct)
                    let mutable current = Unchecked.defaultof<Envelope>
                    let mutable finished = false

                    { new IAsyncEnumerator<Envelope> with
                        member _.Current = current

                        member _.MoveNextAsync() =
                            task {
                                let mutable found = false

                                while not found && not finished && not closed do
                                    try
                                        let! line = input.ReadLineAsync(linked.Token).AsTask()

                                        if isNull line then
                                            finished <- true
                                        else
                                            match Codec.readEnvelope line with
                                            | Ok env ->
                                                current <- env
                                                found <- true
                                            | Error _ -> () // Skip malformed line.
                                    with :? OperationCanceledException ->
                                        finished <- true

                                return found
                            }
                            |> ValueTask<bool>

                        member _.DisposeAsync() =
                            linked.Dispose()
                            ValueTask.CompletedTask
                    }
            }

        member _.CloseAsync(_) =
            closed <- true

            if ownsStreams then
                try
                    input.Dispose()
                with _ ->
                    ()

                try
                    output.Dispose()
                with _ ->
                    ()

            Task.CompletedTask

[<RequireQualifiedAccess>]
module StdioTransport =
    /// Build a `StdioTransport` from the process's `stdin`/`stdout`.
    let fromConsole () : ITransport =
        new StdioTransport(Console.In, Console.Out, ownsStreams = false) :> ITransport
