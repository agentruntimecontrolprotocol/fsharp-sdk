/// Boot three Observer clients on a single producing session.
module ARCP.Samples.Subscriptions.Program

open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open ARCP.Client
open ARCP.Envelope
open ARCP.Ids
open ARCP.Messages.Subscriptions
open ARCP.Samples.Subscriptions.Sinks

let stdoutTypes =
    [
        "log"
        "job.started"
        "job.progress"
        "job.completed"
        "job.failed"
        "tool.error"
    ]

let otlpTypes = [ "metric"; "trace.span" ]

/// Build a SubscribeFilter scoped to one session, optionally filtered by type.
let mkFilter (sessionId: string) (types: string list option) : SubscribeFilter =
    {
        SessionIds = Some [ SessionId.ofString sessionId ]
        Types = types |> Option.defaultValue []
        JobIds = []
        StreamIds = []
        Roles = []
        MinPriority = None
    }

/// Subscribe + drain events into a sink until closed.
let attach (types: string list option) (handler: Envelope<JsonElement> -> Task) : Task =
    task {
        let client: Client = Unchecked.defaultof<_> // transport, identity, auth elided
        // await client.OpenAsync(...)
        let filter = mkFilter "..." types

        match! client.SubscribeAsync(filter, ct = CancellationToken.None) with
        | Error e -> failwithf "subscribe failed: %A" e
        | Ok(sid, events) ->
            try
                do!
                    events
                    |> TaskSeq.iterAsync (fun env ->
                        task {
                            // SubscribeAsync already unwraps subscribe.event payloads —
                            // each yielded envelope is the inner event.
                            do! handler env
                        })
            finally
                client.UnsubscribeAsync(sid).Wait()
    }

[<EntryPoint>]
let main _ =
    let stdout = StdoutSink()
    let otlp = OtlpSink(endpoint = "...")

    task {
        use! sqlite = task { return new SqliteSink(path = "replay.sqlite") }

        do!
            Task.WhenAll(
                attach (Some stdoutTypes) stdout.Handle,
                attach None sqlite.Handle,
                attach (Some otlpTypes) otlp.Handle
            )
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
