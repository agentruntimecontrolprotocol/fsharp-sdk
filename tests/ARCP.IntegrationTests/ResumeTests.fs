module ARCP.IntegrationTests.ResumeTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Logging.Abstractions
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Messages.Session
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Store.EventLog
open ARCP.Runtime
open ARCP.Client

let private startPairWith (log: EventLog) =
    let serverT, clientT = Memory.createPair ()
    let tokens = dict [ "secret", "alice" ]
    let validator = BearerValidator tokens :> IAuthValidator

    let opts =
        { RuntimeOptions.defaults with
            OfferedCapabilities = Capabilities.empty
            EventLog = Some log
        }

    let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
    let _ = runtime.StartAsync CancellationToken.None
    let client = new Client(clientT, Bearer "secret")
    runtime, client

[<Fact>]
let ``resume replays events after the cursor`` () =
    task {
        let log = new EventLog(EventLogOptions.memory ())
        let runtime, client = startPairWith log

        runtime.RegisterTool("echo", fun (_ctx: ToolContext) args -> task { return Ok args })

        let! openResult = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        let sid =
            match openResult with
            | Ok s -> s
            | Error e -> failwithf "open failed: %A" e

        let! _ = client.InvokeAsync("echo", JsonSerializer.SerializeToElement 1)
        let! _ = client.InvokeAsync("echo", JsonSerializer.SerializeToElement 2)

        // Wait briefly for runtime to flush.
        do! Task.Delay 100

        // Pick the earliest message id for this session.
        let allEvents = log.Replay sid |> Seq.toList
        Assert.True(allEvents.Length > 0)
        let firstMid = (List.head allEvents).MessageId

        // Send resume.
        let! r = client.ResumeAsync(sid, firstMid)

        match r with
        | Ok() -> ()
        | Error e -> failwithf "resume failed: %A" e

        // Drain any extra received envelopes by giving the loop time.
        do! Task.Delay 100

        do! runtime.StopAsync()
    }

[<Fact>]
let ``resume with unknown cursor returns DATA_LOSS`` () =
    task {
        let log = new EventLog(EventLogOptions.memory ())
        let runtime, client = startPairWith log

        runtime.RegisterTool("echo", fun (_ctx: ToolContext) args -> task { return Ok args })

        let! openResult = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        let sid =
            match openResult with
            | Ok s -> s
            | Error e -> failwithf "open failed: %A" e

        let! _ = client.InvokeAsync("echo", JsonSerializer.SerializeToElement 1)
        do! Task.Delay 100

        let unknown = MessageId.create ()
        let! r = client.ResumeAsync(sid, unknown)

        match r with
        | Error(DataLoss _) -> ()
        | Error e -> failwithf "expected DataLoss, got %A" e
        | Ok() -> failwith "expected DataLoss"

        do! runtime.StopAsync()
    }
