namespace ARCP.Transport

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open ARCP.Envelope
open ARCP.Messages.Registry

/// <summary>
/// Newline-delimited JSON stdio transport (RFC §22). Each line on the
/// underlying <see cref="TextReader"/>/<see cref="TextWriter"/> is exactly
/// one envelope serialized as JSON.
/// </summary>
module Stdio =

    /// <summary>
    /// <see cref="ITransport"/> implementation over an arbitrary text
    /// reader/writer pair. Concrete bindings: process stdin/stdout
    /// (<see cref="createFromConsole"/>) or in-memory pipes for tests.
    /// </summary>
    type StdioTransport(reader: TextReader, writer: TextWriter) =
        let sendLock = new SemaphoreSlim(1, 1)
        let mutable disposed = false

        interface ITransport with
            /// <summary>Serialize <paramref name="envelope"/> to JSON and emit one line. Thread-safe (RFC §22).</summary>
            member _.SendAsync(envelope, ct) : Task =
                task {
                    let line = Transport.serializeEnvelope envelope
                    do! sendLock.WaitAsync(ct)

                    try
                        do! writer.WriteLineAsync(line.AsMemory(), ct)
                        do! writer.FlushAsync(ct)
                    finally
                        sendLock.Release() |> ignore
                }

            /// <summary>Read one line and parse it as an envelope. Returns <c>None</c> on EOF (RFC §22).</summary>
            member _.ReceiveAsync(ct) : Task<Envelope<MessageType> option> =
                task {
                    let mutable result: Envelope<MessageType> option = None
                    let mutable finished = false

                    while not finished do
                        let! line = reader.ReadLineAsync(ct).AsTask()

                        if isNull line then
                            finished <- true
                        elif String.IsNullOrWhiteSpace line then
                            ()
                        else
                            match Transport.parseEnvelope line with
                            | Ok env ->
                                result <- Some env
                                finished <- true
                            | Error err -> Debug.WriteLine($"ARCP.Stdio: dropping unparseable line: {err}")

                    return result
                }

            /// <summary>Dispose: flush and release the send lock.</summary>
            member _.DisposeAsync() : ValueTask =
                ValueTask(
                    task {
                        if not disposed then
                            disposed <- true

                            try
                                do! writer.FlushAsync()
                            with _ ->
                                ()

                            sendLock.Dispose()
                    }
                )

    /// <summary>Construct a stdio transport over the supplied reader/writer (RFC §22).</summary>
    let create (reader: TextReader, writer: TextWriter) : ITransport =
        new StdioTransport(reader, writer) :> ITransport

    /// <summary>Construct a stdio transport using <c>Console.In</c>/<c>Console.Out</c> (RFC §22).</summary>
    let createFromConsole () : ITransport =
        new StdioTransport(Console.In, Console.Out) :> ITransport

    /// <summary>
    /// Spawn a child process and connect its stdin/stdout as the peer end of
    /// a stdio transport. The returned <see cref="Process"/> is owned by the
    /// caller and must be disposed alongside the transport (RFC §22).
    /// </summary>
    let spawn (executablePath: string, args: string seq) : ITransport * Process =
        let psi = ProcessStartInfo(executablePath)
        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        for a in args do
            psi.ArgumentList.Add a

        match Process.Start psi with
        | null -> failwith "Stdio.spawn: Process.Start returned null"
        | proc ->
            let transport = create (proc.StandardOutput, proc.StandardInput)
            transport, proc
