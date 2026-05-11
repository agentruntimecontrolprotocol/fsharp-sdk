module ARCP.Cli.Program

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Argu
open FSharp.Control
open Microsoft.Extensions.Logging.Abstractions
open ARCP
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Session
open ARCP.Messages.Subscriptions
open ARCP.Messages.Registry
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

// --- argument parser definitions ---

type ServeArgs =
    | [<AltCommandLine("-s")>] Stdio
    | [<AltCommandLine("-w")>] Ws
    | [<AltCommandLine("-p")>] Port of int
    | Token of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Stdio -> "serve over newline-delimited JSON on stdin/stdout"
            | Ws -> "serve over WebSocket"
            | Port _ -> "port for ws (defaults to 7878)"
            | Token _ -> "accept this bearer token (default env ARCP_TOKEN, then any non-empty)"

type TailArgs =
    | [<Mandatory>] Url of string
    | [<Mandatory>] Token of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Url _ -> "ws:// or wss:// URL of the runtime"
            | Token _ -> "bearer token to authenticate with"

type SendArgs =
    | [<Mandatory>] Url of string
    | [<Mandatory>] Token of string
    | [<Mandatory>] Tool of string
    | Args of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Url _ -> "ws:// or wss:// URL of the runtime"
            | Token _ -> "bearer token to authenticate with"
            | Tool _ -> "name of the tool to invoke"
            | Args _ -> "JSON argument value (defaults to null)"

type ReplayArgs =
    | [<Mandatory>] Session of string
    | [<Mandatory>] Db of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Session _ -> "session id (ulid)"
            | Db _ -> "path to the SQLite event log file"

type RootArgs =
    | [<CliPrefix(CliPrefix.None)>] Serve of ParseResults<ServeArgs>
    | [<CliPrefix(CliPrefix.None)>] Tail of ParseResults<TailArgs>
    | [<CliPrefix(CliPrefix.None)>] Send of ParseResults<SendArgs>
    | [<CliPrefix(CliPrefix.None)>] Replay of ParseResults<ReplayArgs>
    | Version

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Serve _ -> "run an in-process ARCP runtime"
            | Tail _ -> "connect to a runtime and print every received envelope"
            | Send _ -> "invoke a single tool on a runtime and print its result"
            | Replay _ -> "replay the SQLite event log for a session"
            | Version -> "print SDK and protocol version"

// --- demo tools used by `arcp serve` ---

let private jsonString (s: string) = JsonSerializer.SerializeToElement<string>(s)

let private registerDemoTools (runtime: Runtime) : unit =
    runtime.RegisterTool("echo", fun (_ctx: ToolContext) args -> task { return Ok args })

    runtime.RegisterTool(
        "progress",
        fun (ctx: ToolContext) _args ->
            task {
                for pct in [ 25; 50; 75 ] do
                    do! ctx.ProgressAsync(Some pct, Some(sprintf "step %d%%" pct))
                    do! Task.Delay(50, ctx.CancellationToken)

                return Ok(jsonString "done")
            }
    )

    runtime.RegisterTool(
        "ask",
        fun (ctx: ToolContext) _args ->
            task {
                let! r =
                    ctx.RequestHumanInputAsync(
                        ("What's your name?",
                         None,
                         Some(jsonString "anonymous"),
                         DateTimeOffset.UtcNow.AddMinutes 5.0,
                         ctx.CancellationToken)
                    )

                match r with
                | Ok v -> return Ok v
                | Error e -> return Error e
            }
    )

let private buildBearerValidator (configuredToken: string option) : IAuthValidator =
    // If a token is configured, only that token is accepted. Otherwise we
    // accept any non-empty bearer token and use the token bytes as the
    // principal name; this matches the "developer mode" affordance the
    // build prompt asks for.
    match configuredToken with
    | Some t when not (String.IsNullOrEmpty t) -> BearerValidator(dict [ t, "developer" ]) :> IAuthValidator
    | _ ->
        { new IAuthValidator with
            member _.ValidateAsync(scheme, _ct) =
                task {
                    match scheme with
                    | Bearer token when not (String.IsNullOrEmpty token) ->
                        return
                            Ok
                                {
                                    Principal = sprintf "dev:%s" token
                                    ExpiresAt = None
                                }
                    | _ -> return Error(Errors.Unauthenticated "expected non-empty bearer token")
                }
        }

let private runtimeOpts () : RuntimeOptions =
    { RuntimeOptions.defaults with
        OfferedCapabilities =
            { Capabilities.empty with
                HumanInput = true
                Subscriptions = true
                Artifacts = true
            }
    }

let private writeLine (text: string) : unit =
    Console.Out.WriteLine text
    Console.Out.Flush()

let private logToStderr (msg: string) : unit =
    Console.Error.WriteLine(sprintf "[arcp] %s" msg)
    Console.Error.Flush()

// --- subcommand implementations ---

let private serveStdio (token: string option) : Task<int> =
    task {
        let transport = Stdio.createFromConsole ()
        let validator = buildBearerValidator token
        let runtime = new Runtime(transport, validator, NullLogger.Instance, runtimeOpts ())
        registerDemoTools runtime
        logToStderr "serve --stdio: ready"
        do! runtime.StartAsync CancellationToken.None
        return 0
    }

let private serveWebSocket (port: int) (token: string option) : Task<int> =
    task {
        let validator = buildBearerValidator token
        let opts = runtimeOpts ()

        let connectionHandler (transport: ITransport) : Task =
            task {
                let runtime = new Runtime(transport, validator, NullLogger.Instance, opts)
                registerDemoTools runtime
                do! runtime.StartAsync CancellationToken.None
            }
            :> Task

        let serverOpts: WebSocket.WebSocketServerOptions =
            {
                Url = sprintf "http://127.0.0.1:%d/" port
                OnConnection = connectionHandler
            }

        let cts = new CancellationTokenSource()
        Console.CancelKeyPress.Add(fun e -> e.Cancel <- true; cts.Cancel())
        let! disposer, uri = WebSocket.runServerAsync serverOpts cts.Token
        let wsUri = WebSocket.toWebSocketUri uri "/ws"
        logToStderr (sprintf "serve --ws: listening on %s" (wsUri.ToString()))

        try
            do! Task.Delay(-1, cts.Token)
        with :? OperationCanceledException ->
            ()

        do! disposer.DisposeAsync()
        return 0
    }

let private tail (url: string) (token: string) : Task<int> =
    task {
        let uri = Uri(url)
        let! ws = WebSocket.ClientWebSocketTransport.ConnectAsync(uri)
        let transport = ws :> ITransport
        let client = new Client(transport, Bearer token)

        let! opened =
            client.OpenAsync(
                { Capabilities.empty with
                    Subscriptions = true
                },
                CancellationToken.None
            )

        match opened with
        | Error e ->
            logToStderr (sprintf "open failed: %A" e)
            return 1
        | Ok _ ->
            let emptyFilter: SubscribeFilter =
                {
                    SessionId = None
                    TraceId = None
                    JobId = None
                    StreamId = None
                    Types = None
                    MinPriority = None
                }

            let! sub = client.SubscribeAsync(emptyFilter)

            match sub with
            | Error e ->
                logToStderr (sprintf "subscribe failed: %A" e)
                return 1
            | Ok(_sid, seq) ->
                let cts = new CancellationTokenSource()
                Console.CancelKeyPress.Add(fun e -> e.Cancel <- true; cts.Cancel())
                let enumerator = seq.GetAsyncEnumerator(cts.Token)

                try
                    let mutable running = true

                    while running && not cts.IsCancellationRequested do
                        let! moved =
                            task {
                                try
                                    return! enumerator.MoveNextAsync().AsTask()
                                with _ ->
                                    return false
                            }

                        if not moved then
                            running <- false
                        else
                            let env = enumerator.Current
                            writeLine (env.Payload.GetRawText())
                finally
                    let _ = enumerator.DisposeAsync()
                    ()

                return 0
    }

let private send (url: string) (token: string) (tool: string) (argsJson: string option) : Task<int> =
    task {
        let uri = Uri(url)
        let! ws = WebSocket.ClientWebSocketTransport.ConnectAsync(uri)
        let transport = ws :> ITransport
        let client = new Client(transport, Bearer token)
        let! opened = client.OpenAsync(Capabilities.empty, CancellationToken.None)

        match opened with
        | Error e ->
            logToStderr (sprintf "open failed: %A" e)
            return 1
        | Ok _ ->
            let argsElement =
                match argsJson with
                | Some s ->
                    use doc = JsonDocument.Parse s
                    doc.RootElement.Clone()
                | None ->
                    use doc = JsonDocument.Parse "null"
                    doc.RootElement.Clone()

            let! result = client.InvokeAsync(tool, argsElement)

            match result with
            | Ok(Some v) ->
                writeLine (v.GetRawText())
                return 0
            | Ok None ->
                writeLine "null"
                return 0
            | Error e ->
                logToStderr (sprintf "invoke failed: %A" e)
                return 1
    }

let private replay (sessionId: string) (dbPath: string) : Task<int> =
    task {
        let log =
            new Store.EventLog.EventLog(Store.EventLog.EventLogOptions.file dbPath)

        let sid = SessionId.ofString sessionId

        let events = log.Replay sid

        for ev in events do
            writeLine ev.EnvelopeJson

        return 0
    }

// --- entry point ---

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<RootArgs>(programName = "arcp")

    try
        let results = parser.Parse(argv, raiseOnUsage = false)

        if results.IsUsageRequested then
            writeLine (parser.PrintUsage())
            0
        else
            let exitCode =
                match results.GetAllResults() with
                | [] ->
                    writeLine (parser.PrintUsage())
                    0
                | Version :: _ ->
                    writeLine (sprintf "arcp %s (protocol %s)" Version.Sdk Version.Protocol)
                    0
                | Serve sub :: _ ->
                    let token =
                        sub.TryGetResult ServeArgs.Token
                        |> Option.orElseWith (fun () ->
                            let env = Environment.GetEnvironmentVariable "ARCP_TOKEN"
                            if String.IsNullOrEmpty env then None else Some env)

                    let stdio = sub.Contains ServeArgs.Stdio
                    let ws = sub.Contains ServeArgs.Ws

                    if stdio = ws then
                        logToStderr "serve requires exactly one of --stdio or --ws"
                        2
                    elif stdio then
                        (serveStdio token).GetAwaiter().GetResult()
                    else
                        let port = sub.GetResult(ServeArgs.Port, defaultValue = 7878)
                        (serveWebSocket port token).GetAwaiter().GetResult()
                | Tail sub :: _ ->
                    let url = sub.GetResult TailArgs.Url
                    let token = sub.GetResult TailArgs.Token
                    (tail url token).GetAwaiter().GetResult()
                | Send sub :: _ ->
                    let url = sub.GetResult SendArgs.Url
                    let token = sub.GetResult SendArgs.Token
                    let tool = sub.GetResult SendArgs.Tool
                    let argsJson = sub.TryGetResult SendArgs.Args
                    (send url token tool argsJson).GetAwaiter().GetResult()
                | Replay sub :: _ ->
                    let s = sub.GetResult ReplayArgs.Session
                    let db = sub.GetResult ReplayArgs.Db
                    (replay s db).GetAwaiter().GetResult()

            exitCode
    with
    | :? ArguParseException as ex ->
        logToStderr ex.Message
        2
    | ex ->
        logToStderr (sprintf "fatal: %s" ex.Message)
        1
