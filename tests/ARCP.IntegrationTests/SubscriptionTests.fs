module ARCP.IntegrationTests.SubscriptionTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Xunit
open FSharp.Control
open Microsoft.Extensions.Logging.Abstractions
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Session
open ARCP.Messages.Subscriptions
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

let private startPair () =
    let serverT, clientT = Memory.createPair ()

    let tokens = dict [ "secret", "alice" ]
    let validator = BearerValidator tokens :> IAuthValidator

    let opts =
        { RuntimeOptions.defaults with
            OfferedCapabilities =
                { Capabilities.empty with
                    Subscriptions = true
                }
        }

    let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
    let _ = runtime.StartAsync CancellationToken.None
    let client = new Client(clientT, Bearer "secret")
    runtime, client

let private emptyFilter: SubscribeFilter =
    {
        SessionId = None
        TraceId = None
        JobId = None
        StreamId = None
        Types = None
        MinPriority = None
    }

let private waitForEvent
    (seq: System.Collections.Generic.IAsyncEnumerable<Envelope<JsonElement>>)
    (predicate: Envelope<JsonElement> -> bool)
    (timeoutMs: int)
    : Task<Envelope<JsonElement> option> =
    task {
        let cts = new CancellationTokenSource(timeoutMs)
        let mutable found: Envelope<JsonElement> option = None

        try
            let enumerator = seq.GetAsyncEnumerator(cts.Token)

            try
                let mutable keepGoing = true

                while keepGoing && found.IsNone do
                    let! moved = enumerator.MoveNextAsync().AsTask()

                    if not moved then
                        keepGoing <- false
                    else
                        let cur = enumerator.Current

                        if predicate cur then
                            found <- Some cur
            finally
                let _ = enumerator.DisposeAsync()
                ()
        with _ ->
            ()

        return found
    }

[<Fact>]
let ``subscribe with empty filter receives live job lifecycle events`` () =
    task {
        let runtime, client = startPair ()

        runtime.RegisterTool("echo", fun (_ctx: ToolContext) args -> task { return Ok args })

        let! _ =
            client.OpenAsync(
                { Capabilities.empty with
                    Subscriptions = true
                },
                CancellationToken.None
            )

        let! sub = client.SubscribeAsync(emptyFilter)

        match sub with
        | Ok(_sid, seq) ->
            // give backfill-complete a moment
            do! Task.Delay 50
            let! _ = client.InvokeAsync("echo", JsonSerializer.SerializeToElement(1))

            let! found =
                waitForEvent
                    seq
                    (fun e ->
                        let kind =
                            try
                                e.Payload.GetProperty("type").GetString()
                            with _ ->
                                ""

                        kind = "job.completed" || kind = "job.accepted" || kind = "job.started")
                    2000

            Assert.True(found.IsSome, "expected a job lifecycle event via subscription")
        | Error e -> failwithf "subscribe failed: %A" e

        do! runtime.StopAsync()
    }

[<Fact>]
let ``backfill emits prior events then backfill_complete then live`` () =
    task {
        let runtime, client = startPair ()
        runtime.RegisterTool("echo", fun (_ctx: ToolContext) args -> task { return Ok args })

        let! _ =
            client.OpenAsync(
                { Capabilities.empty with
                    Subscriptions = true
                },
                CancellationToken.None
            )
        // run a job to populate log
        let! _ = client.InvokeAsync("echo", JsonSerializer.SerializeToElement(1))
        do! Task.Delay 100

        let! sub = client.SubscribeAsync(emptyFilter)

        match sub with
        | Ok(_sid, seq) ->
            let! found =
                waitForEvent
                    seq
                    (fun e ->
                        try
                            e.Payload.GetProperty("type").GetString() = "subscription.backfill_complete"
                        with _ ->
                            false)
                    2000

            Assert.True(found.IsSome, "expected backfill_complete synthetic event")
        | Error e -> failwithf "subscribe failed: %A" e

        do! runtime.StopAsync()
    }

[<Fact>]
let ``filter by types delivers only matching messages`` () =
    task {
        let runtime, client = startPair ()
        runtime.RegisterTool("echo", fun (_ctx: ToolContext) args -> task { return Ok args })

        let! _ =
            client.OpenAsync(
                { Capabilities.empty with
                    Subscriptions = true
                },
                CancellationToken.None
            )

        let filter =
            { emptyFilter with
                Types = Some [ "job.progress" ]
            }

        let! sub = client.SubscribeAsync(filter)

        match sub with
        | Ok(_sid, seq) ->
            do! Task.Delay 50
            let! _ = client.InvokeAsync("echo", JsonSerializer.SerializeToElement(1))

            // Try to receive any non-synthetic event; should not see job.completed/started.
            let cts = new CancellationTokenSource(500)
            let mutable sawDisallowed = false

            try
                let enumerator = seq.GetAsyncEnumerator(cts.Token)

                try
                    let mutable keepGoing = true

                    while keepGoing do
                        let! moved = enumerator.MoveNextAsync().AsTask()

                        if not moved then
                            keepGoing <- false
                        else
                            let cur = enumerator.Current

                            let kind =
                                try
                                    cur.Payload.GetProperty("type").GetString()
                                with _ ->
                                    ""

                            if kind = "job.completed" || kind = "job.started" || kind = "job.accepted" then
                                sawDisallowed <- true
                                keepGoing <- false
                finally
                    let _ = enumerator.DisposeAsync()
                    ()
            with _ ->
                ()

            Assert.False(sawDisallowed, "filter should have excluded non-progress events")
        | Error e -> failwithf "subscribe failed: %A" e

        do! runtime.StopAsync()
    }

[<Fact>]
let ``subscribe with another principal session id is denied`` () =
    task {
        let runtime, client = startPair ()

        let! _ =
            client.OpenAsync(
                { Capabilities.empty with
                    Subscriptions = true
                },
                CancellationToken.None
            )

        let filter =
            { emptyFilter with
                SessionId = Some [ SessionId.ofString "someone-else-session" ]
            }

        let! sub = client.SubscribeAsync(filter)

        match sub with
        | Ok _ -> failwith "expected permission denial"
        | Error _ -> ()

        do! runtime.StopAsync()
    }

[<Fact>]
let ``backpressure overflow drops subscription with BACKPRESSURE_OVERFLOW`` () =
    // Directly drive a SubscriptionManager with a tiny channel by sending many
    // matching envelopes through PublishAsync until overflow.
    task {
        let log =
            new ARCP.Store.EventLog.EventLog(ARCP.Store.EventLog.EventLogOptions.memory ())

        let collected =
            ResizeArray<ARCP.Envelope.Envelope<ARCP.Messages.Registry.MessageType>>()

        let send env =
            collected.Add env
            Task.CompletedTask

        use mgr = new SubscriptionManager(log, send)
        let sid = SessionId.create ()

        let! _subId = mgr.SubscribeAsync(sid, "alice", emptyFilter, None, (fun _ -> Some "alice"), capacity = 2)

        // Publish many envelopes; first two fill the channel, third causes
        // overflow and emits subscribe.closed BACKPRESSURE_OVERFLOW.
        for _ in 1..10 do
            let env =
                Envelope.create
                    "job.progress"
                    (ARCP.Messages.Registry.MessageType.JobProgress { Percent = Some 1; Message = None })
                |> Envelope.withSession sid

            do! mgr.PublishAsync env

        let closed =
            collected
            |> Seq.exists (fun e ->
                e.Type = "subscribe.closed"
                && match e.Payload with
                   | ARCP.Messages.Registry.MessageType.SubscribeClosed c -> c.Code = Some "BACKPRESSURE_OVERFLOW"
                   | _ -> false)

        Assert.True(closed, "expected BACKPRESSURE_OVERFLOW subscribe.closed envelope")
    }
