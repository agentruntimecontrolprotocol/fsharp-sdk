module ARCP.IntegrationTests.HumanInputTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Time.Testing
open ARCP
open ARCP.Errors
open ARCP.Messages.Session
open ARCP.Messages.Human
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

let private startPair (timeProvider: TimeProvider) =
    let serverT, clientT = Memory.createPair ()
    let tokens = dict [ "secret", "alice" ]
    let validator = BearerValidator tokens :> IAuthValidator

    let opts =
        { RuntimeOptions.defaults with
            TimeProvider = timeProvider
            LeaseSweepInterval = TimeSpan.FromMilliseconds 100.0
        }

    let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
    let _ = runtime.StartAsync CancellationToken.None
    let client = new Client(clientT, Bearer "secret")
    runtime, client

let private jsonObj (json: string) : JsonElement =
    JsonSerializer.Deserialize<JsonElement>(json)

let private jsonString (s: string) : JsonElement =
    JsonSerializer.SerializeToElement<string>(s)

[<Fact>]
let ``human input round-trip: client handler returns value`` () =
    task {
        let runtime, client = startPair TimeProvider.System

        let handler =
            { new IHumanInputHandler with
                member _.HandleAsync(_prompt, _schema, _dflt, _expiresAt, _ct) =
                    task { return jsonObj """{"branch":"fix/x"}""" }
            }

        client.HumanInputHandler <- Some handler

        runtime.RegisterTool(
            "ask",
            fun (ctx: ToolContext) _ ->
                task {
                    let! v =
                        ctx.RequestHumanInputAsync(
                            ("branch?", None, None, DateTimeOffset.UtcNow.AddMinutes 5.0, ctx.CancellationToken)
                        )

                    return Ok v
                }
        )

        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)
        let! result = client.InvokeAsync("ask", jsonString "go")

        match result with
        | Ok(Some v) -> Assert.Equal("fix/x", v.GetProperty("branch").GetString())
        | other -> failwithf "expected branch=fix/x, got %A" other

        do! runtime.StopAsync()
    }

[<Fact>]
let ``invalid response against schema -> runtime sends nack`` () =
    task {
        let runtime, client = startPair TimeProvider.System

        // Schema requires an object with a "branch" string.
        let schema =
            jsonObj """{"type":"object","required":["branch"],"properties":{"branch":{"type":"string"}}}"""

        let handler =
            { new IHumanInputHandler with
                member _.HandleAsync(_prompt, _schema, _dflt, _expiresAt, _ct) =
                    // Return something that fails the schema (missing required prop).
                    task { return jsonObj """{"unrelated":1}""" }
            }

        client.HumanInputHandler <- Some handler

        runtime.RegisterTool(
            "ask",
            fun (ctx: ToolContext) _ ->
                task {
                    try
                        let! v =
                            ctx.RequestHumanInputAsync(
                                ("branch?",
                                 Some schema,
                                 None,
                                 DateTimeOffset.UtcNow.AddMilliseconds 250.0,
                                 ctx.CancellationToken)
                            )

                        return Ok v
                    with _ ->
                        return Error(DeadlineExceeded "no valid response")
                }
        )

        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)
        let! result = client.InvokeAsync("ask", jsonString "go")

        // The invalid response should fail validation, runtime nacks, and the
        // tight expiresAt then drives a DEADLINE_EXCEEDED on the tool side.
        match result with
        | Error(DeadlineExceeded _)
        | Error(Internal _) -> ()
        | other -> failwithf "expected DeadlineExceeded or Internal, got %A" other

        do! runtime.StopAsync()
    }

[<Fact>]
let ``expiration with default synthesizes default response`` () =
    task {
        let fake = FakeTimeProvider(DateTimeOffset.UtcNow)
        let runtime, client = startPair fake

        // Handler never responds.
        let handler =
            { new IHumanInputHandler with
                member _.HandleAsync(_prompt, _schema, _dflt, _expiresAt, ct) =
                    task {
                        let tcs = TaskCompletionSource<JsonElement>()
                        use _ = ct.Register(fun () -> tcs.TrySetCanceled() |> ignore)
                        return! tcs.Task
                    }
            }

        client.HumanInputHandler <- Some handler

        let dflt = jsonString "fallback"

        let expiresAt = fake.GetUtcNow().AddSeconds 5.0

        runtime.RegisterTool(
            "ask",
            fun (ctx: ToolContext) _ ->
                task {
                    let! v = ctx.RequestHumanInputAsync(("branch?", None, Some dflt, expiresAt, ctx.CancellationToken))

                    return Ok v
                }
        )

        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        let invoke = client.InvokeAsync("ask", jsonString "go")

        // Allow the tool to register its pending entry, then advance time.
        do! Task.Delay(50)
        fake.Advance(TimeSpan.FromSeconds 6.0)

        let! result = invoke

        match result with
        | Ok(Some v) -> Assert.Equal("fallback", v.GetString())
        | other -> failwithf "expected default fallback, got %A" other

        do! runtime.StopAsync()
    }

[<Fact>]
let ``expiration without default -> tool gets DeadlineExceeded`` () =
    task {
        let fake = FakeTimeProvider(DateTimeOffset.UtcNow)
        let runtime, client = startPair fake

        let handler =
            { new IHumanInputHandler with
                member _.HandleAsync(_prompt, _schema, _dflt, _expiresAt, ct) =
                    task {
                        let tcs = TaskCompletionSource<JsonElement>()
                        use _ = ct.Register(fun () -> tcs.TrySetCanceled() |> ignore)
                        return! tcs.Task
                    }
            }

        client.HumanInputHandler <- Some handler

        let expiresAt = fake.GetUtcNow().AddSeconds 5.0

        runtime.RegisterTool(
            "ask",
            fun (ctx: ToolContext) _ ->
                task {
                    try
                        let! v = ctx.RequestHumanInputAsync(("branch?", None, None, expiresAt, ctx.CancellationToken))

                        return Ok v
                    with :? TimeoutException ->
                        return Error(DeadlineExceeded "human input")
                }
        )

        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        let invoke = client.InvokeAsync("ask", jsonString "go")

        do! Task.Delay(50)
        fake.Advance(TimeSpan.FromSeconds 6.0)

        let! result = invoke

        match result with
        | Error(DeadlineExceeded _) -> ()
        | other -> failwithf "expected DeadlineExceeded, got %A" other

        do! runtime.StopAsync()
    }
