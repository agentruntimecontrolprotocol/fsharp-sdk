module ARCP.Samples.RelayHumanInTheLoop.Program

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open ARCP.Messages.Session
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

let private jsonString (s: string) =
    JsonSerializer.SerializeToElement<string>(s)

/// <summary>
/// A relay handler emulating the §12.3 multi-channel pattern: it fans the
/// <c>human.input.request</c> out to two "channels" (e.g. phone + chat),
/// races their responders, and returns the winner. The loser is notified
/// via a cancellation token so it can clean up — the analog of the runtime
/// sending <c>human.input.cancelled</c> down the unused channel.
/// </summary>
type RelayHandler(phoneDelayMs: int, chatDelayMs: int) =
    interface IHumanInputHandler with
        member _.HandleAsync(prompt, _schema, _dflt, _expiresAt, ct) =
            task {
                printfn "relay: fan-out prompt '%s' to phone+chat" prompt

                let loserCts = new CancellationTokenSource()
                use linked = CancellationTokenSource.CreateLinkedTokenSource(ct, loserCts.Token)

                let phone =
                    task {
                        try
                            do! Task.Delay(phoneDelayMs, linked.Token)
                            return Some("phone", jsonString "Alice (via phone)")
                        with :? OperationCanceledException ->
                            printfn "relay: phone channel cancelled (loser)"
                            return None
                    }

                let chat =
                    task {
                        try
                            do! Task.Delay(chatDelayMs, linked.Token)
                            return Some("chat", jsonString "Alice (via chat)")
                        with :? OperationCanceledException ->
                            printfn "relay: chat channel cancelled (loser)"
                            return None
                    }

                let! winner = Task.WhenAny(phone, chat)
                let! winResult = winner

                match winResult with
                | Some(channel, value) ->
                    printfn "relay: winner=%s" channel
                    loserCts.Cancel()

                    try
                        do! Task.WhenAll([| phone :> Task; chat :> Task |]).WaitAsync(TimeSpan.FromMilliseconds 250.0)
                    with _ ->
                        ()

                    return value
                | None ->
                    printfn "relay: both channels gave up"
                    return jsonString "(no answer)"
            }

/// <summary>
/// Sample 06 — demonstrates the §12.3 human-in-the-loop relay scenario.
/// The runtime tool asks the human a question; the client's
/// <see cref="RelayHandler"/> races two simulated responders; the slower
/// channel observes the cancellation analog.
/// </summary>
[<EntryPoint>]
let main _argv =
    task {
        let serverT, clientT = Memory.createPair ()
        let tokens = dict [ "secret", "alice" ]
        let validator = BearerValidator tokens :> IAuthValidator

        let opts =
            { RuntimeOptions.defaults with
                OfferedCapabilities =
                    { Capabilities.empty with
                        HumanInput = true
                    }
            }

        let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
        let _ = runtime.StartAsync CancellationToken.None

        runtime.RegisterTool(
            "ask-name",
            fun (ctx: ToolContext) _ ->
                task {
                    let! r =
                        ctx.RequestHumanInputAsync(
                            ("who am I talking to?",
                             None,
                             None,
                             DateTimeOffset.UtcNow.AddMinutes 1.0,
                             ctx.CancellationToken)
                        )

                    match r with
                    | Ok v -> return Ok v
                    | Error e -> return Error e
                }
        )

        let client = new Client(clientT, Bearer "secret")
        client.HumanInputHandler <- Some(RelayHandler(50, 250))

        let! _ =
            client.OpenAsync(
                { Capabilities.empty with
                    HumanInput = true
                },
                CancellationToken.None
            )

        let! result = client.InvokeAsync("ask-name", jsonString "go")

        do! runtime.StopAsync()
        do! (runtime :> IAsyncDisposable).DisposeAsync()
        do! (client :> IAsyncDisposable).DisposeAsync()

        match result with
        | Ok(Some v) ->
            printfn "tool received: %s" (v.GetString())
            return 0
        | other ->
            eprintfn "unexpected: %A" other
            return 1
    }
    |> fun t -> t.GetAwaiter().GetResult()
