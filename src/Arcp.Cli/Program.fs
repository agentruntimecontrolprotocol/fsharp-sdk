module ARCP.Cli.Program

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Argu
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport
open ARCP.Runtime

type ServeArgs =
    | [<AltCommandLine("-s")>] Stdio
    | Token of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Stdio -> "serve over newline-delimited JSON on stdin/stdout"
            | Token _ -> "accept this bearer token (default: any non-empty)"

type SendArgs =
    | [<Mandatory>] Url of string
    | [<Mandatory>] Agent of string
    | Token of string
    | Input of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Url _ -> "ws:// or wss:// URL of the runtime"
            | Token _ -> "bearer token (default: read from $ARCP_TOKEN)"
            | Agent _ -> "agent identifier (name or name@version)"
            | Input _ -> "JSON input passed to the agent (default: null)"

type RootArgs =
    | [<CliPrefix(CliPrefix.None)>] Serve of ParseResults<ServeArgs>
    | [<CliPrefix(CliPrefix.None)>] Send of ParseResults<SendArgs>
    | Version

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Serve _ -> "run an in-process ARCP runtime"
            | Send _ -> "submit a job to a runtime and stream its events"
            | Version -> "print SDK and protocol version"

let private writeLine (text: string) = Console.Out.WriteLine text

let private errorLine (text: string) =
    Console.Error.WriteLine("[arcp] " + text)

let private buildVerifier (token: string option) =
    match token with
    | Some t when not (String.IsNullOrEmpty t) ->
        ARCP.Runtime.Auth.StaticBearerVerifier(readOnlyDict [ t, "developer" ]) :> ARCP.Runtime.Auth.IBearerVerifier
    | _ -> ARCP.Runtime.Auth.DevModeBearerVerifier() :> ARCP.Runtime.Auth.IBearerVerifier

let private serveStdio (token: string option) : Task<int> =
    task {
        let options =
            { ArcpServerOptions.defaults with
                BearerVerifier = buildVerifier token
            }

        let server = ArcpServer(options)
        server.RegisterAgent("echo", fun ctx -> task { return Json.serializeToElement<string> "echo" })
        let transport = StdioTransport.fromConsole ()
        errorLine "serve --stdio: ready"
        do! server.HandleSessionAsync(transport, CancellationToken.None)
        return 0
    }

let private streamEventsAsync (handle: JobHandle) : Task =
    task {
        let enumerator = handle.Events.GetAsyncEnumerator(CancellationToken.None)

        try
            let mutable more = true

            while more do
                let! has = enumerator.MoveNextAsync().AsTask()

                if not has then
                    more <- false
                else
                    writeLine (sprintf "event: %s" (JobEventBody.kind enumerator.Current))
        finally
            ignore (enumerator.DisposeAsync().AsTask())
    }
    :> Task

let private buildSubmit (agent: string) (inputJson: string option) : JobSubmitRequest =
    let input =
        match inputJson with
        | Some s -> Json.parseElement s
        | None -> Json.nullElement ()

    {
        Agent = agent
        Input = input
        LeaseRequest = None
        LeaseConstraints = None
        IdempotencyKey = None
        MaxRuntimeSec = None
    }

let private send (url: string) (token: string option) (agent: string) (inputJson: string option) : Task<int> =
    task {
        let! transport = WebSocketClientTransport.connectAsync (Uri url) token CancellationToken.None

        let options =
            { ArcpClientOptions.defaults with
                Auth =
                    match token with
                    | Some t -> AuthScheme.Bearer t
                    | None -> AuthScheme.None
            }

        let client = new ArcpClient(transport, options)
        let! _ = client.ConnectAsync CancellationToken.None
        let! handle = client.SubmitAsync(buildSubmit agent inputJson, CancellationToken.None)
        do! streamEventsAsync handle
        let! result = handle.Result

        match result with
        | Ok r ->
            match r.Result with
            | Some v -> writeLine (v.GetRawText())
            | None -> writeLine "null"

            return 0
        | Error e ->
            errorLine (sprintf "job failed: %s — %s" (ARCPError.code e) (ARCPError.message e))
            return 1
    }

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<RootArgs>(programName = "arcp")

    try
        let results = parser.Parse(argv, raiseOnUsage = false)

        if results.IsUsageRequested then
            writeLine (parser.PrintUsage())
            0
        else
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

                (serveStdio token).GetAwaiter().GetResult()
            | Send sub :: _ ->
                let url = sub.GetResult SendArgs.Url

                let token =
                    sub.TryGetResult SendArgs.Token
                    |> Option.orElseWith (fun () ->
                        let env = Environment.GetEnvironmentVariable "ARCP_TOKEN"
                        if String.IsNullOrEmpty env then None else Some env)

                let agent = sub.GetResult SendArgs.Agent
                let input = sub.TryGetResult SendArgs.Input
                (send url token agent input).GetAwaiter().GetResult()
    with
    | :? ArguParseException as ex ->
        errorLine ex.Message
        2
    | ex ->
        errorLine (sprintf "fatal: %s" ex.Message)
        1
