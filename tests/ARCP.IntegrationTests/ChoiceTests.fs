module ARCP.IntegrationTests.ChoiceTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Logging.Abstractions
open ARCP
open ARCP.Errors
open ARCP.Messages.Session
open ARCP.Messages.Human
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

let private jsonString (s: string) : JsonElement =
    JsonSerializer.SerializeToElement<string>(s)

let private startPair () =
    let serverT, clientT = Memory.createPair ()
    let tokens = dict [ "secret", "alice" ]
    let validator = BearerValidator tokens :> IAuthValidator

    let opts =
        { RuntimeOptions.defaults with
            LeaseSweepInterval = TimeSpan.FromMilliseconds 200.0
        }

    let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
    let _ = runtime.StartAsync CancellationToken.None
    let client = new Client(clientT, Bearer "secret")
    runtime, client

[<Fact>]
let ``choice round-trip: client picks one of three options`` () =
    task {
        let runtime, client = startPair ()

        let handler =
            { new IChoiceHandler with
                member _.HandleAsync(_prompt, _options, _expiresAt, _ct) = task { return "fix" }
            }

        client.ChoiceHandler <- Some handler

        let choices =
            [
                { Id = "fix"; Label = "Fix" }
                { Id = "skip"; Label = "Skip" }
                { Id = "abort"; Label = "Abort" }
            ]

        runtime.RegisterTool(
            "pick",
            fun (ctx: ToolContext) _ ->
                task {
                    let! id =
                        ctx.RequestChoiceAsync(
                            ("which?", choices, DateTimeOffset.UtcNow.AddMinutes 5.0, ctx.CancellationToken)
                        )

                    return Ok(jsonString id)
                }
        )

        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)
        let! result = client.InvokeAsync("pick", jsonString "go")

        match result with
        | Ok(Some v) -> Assert.Equal("fix", v.GetString())
        | other -> failwithf "expected fix, got %A" other

        do! runtime.StopAsync()
    }
