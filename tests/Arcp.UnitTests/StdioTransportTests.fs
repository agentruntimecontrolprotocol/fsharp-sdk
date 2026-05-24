module ARCP.UnitTests.StdioTransportTests

open System.IO
open System.Text
open System.Threading
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport

let private envelope (id: string) =
    let payload: SessionPingPayload =
        {
            Nonce = "n"
            SentAt = System.DateTimeOffset.UnixEpoch
        }

    Envelope.create "session.ping" (Json.serializeToElement payload)
    |> Envelope.withId id

let private readEnvelopes (transport: ITransport) (ct: CancellationToken) =
    task {
        let enumerable = transport.Receive ct
        let enumerator = enumerable.GetAsyncEnumerator ct
        let results = ResizeArray<Envelope>()

        try
            let mutable more = true

            while more do
                let! has = enumerator.MoveNextAsync().AsTask()

                if has then
                    results.Add enumerator.Current
                else
                    more <- false
        finally
            ignore (enumerator.DisposeAsync().AsTask())

        return results
    }

[<Fact>]
let ``StdioTransport serialises envelopes as newline-delimited JSON`` () =
    let outBuf = new StringWriter()
    let transport: ITransport = new StdioTransport(new StringReader(""), outBuf, ownsStreams = false)
    transport.SendAsync(envelope "m1", CancellationToken.None).Wait()
    transport.SendAsync(envelope "m2", CancellationToken.None).Wait()
    let text = outBuf.ToString()
    let lines = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
    lines |> Array.length |> should equal 2
    lines.[0].Contains "\"id\":\"m1\"" |> should equal true
    lines.[1].Contains "\"id\":\"m2\"" |> should equal true

[<Fact>]
let ``StdioTransport reads newline-delimited envelopes from input`` () =
    let json = Codec.writeEnvelope (envelope "m1") + "\n" + Codec.writeEnvelope (envelope "m2") + "\n"

    let transport: ITransport =
        new StdioTransport(new StringReader(json), new StringWriter(), ownsStreams = false)

    let envelopes = (readEnvelopes transport CancellationToken.None).Result
    envelopes.Count |> should equal 2
    envelopes.[0].Id |> should equal "m1"
    envelopes.[1].Id |> should equal "m2"

[<Fact>]
let ``StdioTransport skips malformed lines and continues`` () =
    let good = Codec.writeEnvelope (envelope "ok")
    let json = "{not json\n" + good + "\n"

    let transport: ITransport =
        new StdioTransport(new StringReader(json), new StringWriter(), ownsStreams = false)

    let envelopes = (readEnvelopes transport CancellationToken.None).Result
    envelopes.Count |> should equal 1
    envelopes.[0].Id |> should equal "ok"

[<Fact>]
let ``StdioTransport CloseAsync without owning streams leaves them open`` () =
    let reader = new StringReader("")
    let writer = new StringWriter()
    let transport: ITransport = new StdioTransport(reader, writer, ownsStreams = false)
    transport.CloseAsync(CancellationToken.None).Wait()
    // Should not throw — streams remain usable.
    writer.Write 'x'
    writer.ToString() |> should equal "x"

[<Fact>]
let ``StdioTransport CloseAsync owning streams disposes them`` () =
    let reader = new StringReader("")
    let writer = new StringWriter()
    let transport: ITransport = new StdioTransport(reader, writer, ownsStreams = true)
    transport.CloseAsync(CancellationToken.None).Wait()
    // After dispose, writing throws ObjectDisposedException.
    Assert.Throws<System.ObjectDisposedException>(fun () -> writer.Write 'x') |> ignore

[<Fact>]
let ``StdioTransport fromConsole returns a working transport`` () =
    let transport = StdioTransport.fromConsole ()
    Assert.NotNull(transport)
    transport.CloseAsync(CancellationToken.None).Wait()
