module ARCP.Samples.HumanInputRequest.Program

open System
open System.Text.Json
open System.Threading
open Microsoft.Extensions.Logging.Abstractions
open ARCP.Messages.Session
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

let private jsonString (s: string) =
    JsonSerializer.SerializeToElement<string>(s)

let private parseJson (s: string) =
    use doc = JsonDocument.Parse s
    doc.RootElement.Clone()

/// <summary>
/// Sample 03 — a tool asks the human for input via
/// <c>ctx.RequestHumanInputAsync</c> (RFC §14.1). The client installs an
/// <see cref="IHumanInputHandler"/> that always answers <c>"red"</c>, and
/// the tool returns the answer to the caller.
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

        let schema = parseJson """{"type":"string"}"""

        runtime.RegisterTool(
            "ask-color",
            fun (ctx: ToolContext) _ ->
                task {
                    let! r =
                        ctx.RequestHumanInputAsync(
                            ("favorite color?",
                             Some schema,
                             Some(jsonString "blue"),
                             DateTimeOffset.UtcNow.AddMinutes 5.0,
                             ctx.CancellationToken)
                        )

                    match r with
                    | Ok v -> return Ok v
                    | Error e -> return Error e
                }
        )

        let handler =
            { new IHumanInputHandler with
                member _.HandleAsync(prompt, _schema, _dflt, _expiresAt, _ct) =
                    task {
                        printfn "human got prompt: %s" prompt
                        return jsonString "red"
                    }
            }

        let client = new Client(clientT, Bearer "secret")
        client.HumanInputHandler <- Some handler

        let! _ =
            client.OpenAsync(
                { Capabilities.empty with
                    HumanInput = true
                },
                CancellationToken.None
            )

        let! result = client.InvokeAsync("ask-color", jsonString "go")

        do! runtime.StopAsync()
        do! (runtime :> IAsyncDisposable).DisposeAsync()
        do! (client :> IAsyncDisposable).DisposeAsync()

        match result with
        | Ok(Some v) ->
            printfn "tool received human input: %s" (v.GetString())
            return 0
        | other ->
            eprintfn "unexpected: %A" other
            return 1
    }
    |> fun t -> t.GetAwaiter().GetResult()
