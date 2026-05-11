/// Fan `human.input.request` across channels; resolve on first.
module ARCP.Samples.HumanInput.Program

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open ARCP.Client
open ARCP.Envelope
open ARCP.Samples.HumanInput.Channels

let destinations = [ "ntfy:phone"; "email:oncall"; "slack:ops" ]

let parseTimestamp (v: string) : DateTimeOffset =
    DateTimeOffset.Parse(v.Replace("Z", "+00:00"))

/// Race all channels; first non-cancelled completion wins.
let fanOut (client: Client) (request: Envelope<JsonElement>) : Task =
    task {
        let payload = request.Payload

        let schema =
            match payload.TryGetProperty "response_schema" with
            | true, v -> v
            | _ -> JsonDocument.Parse("{}").RootElement

        let prompt = payload.GetProperty("prompt").GetString()
        let expiresAt = parseTimestamp (payload.GetProperty("expires_at").GetString())
        let timeout = max TimeSpan.Zero (expiresAt - DateTimeOffset.UtcNow)

        use cts = new CancellationTokenSource(timeout)

        let tasks =
            destinations
            |> List.map (fun dest ->
                let chan = registry.[dest]

                task {
                    let! v = chan prompt schema cts.Token
                    return dest, v
                })
            |> List.toArray

        let! winner =
            try
                Task.WhenAny(tasks |> Array.map (fun t -> t :> Task))
            with _ ->
                Task.FromResult(Unchecked.defaultof<Task>)

        // Cancel losers.
        cts.Cancel()

        if isNull winner || winner.IsFaulted || winner.IsCanceled then
            // Deadline elapsed; translate timeout into the cancelled-input shape (RFC §12.4).
            // client.SendHumanInputCancelledAsync(corr = request.Id, code = "DEADLINE_EXCEEDED",
            //                                     message = "no channel responded before expires_at")
            return ()
        else
            let won = (winner :?> Task<string * JsonElement>).Result
            let respondedBy, value = won
            // client.SendHumanInputResponseAsync(corr = request.Id, value = value,
            //                                    respondedBy = respondedBy, respondedAt = DateTimeOffset.UtcNow)

            // Tell the losing destinations the question is settled.
            let losers =
                Array.zip (List.toArray destinations) tasks
                |> Array.choose (fun (d, t) -> if t :> Task = winner then None else Some d)

            if losers.Length > 0 then
                // client.SendHumanInputCancelledAsync(corr = request.Id, code = "OK",
                //                                     message = "answered elsewhere", channels = losers)
                ()

            return ()
    }
    :> Task

[<EntryPoint>]
let main _ =
    task {
        let client: Client = Unchecked.defaultof<_> // transport, identity, auth elided

        // for env in client.Events do
        //   if env.Type = "human.input.request" then
        //     let _ = Task.Run(fun () -> fanOut client env)
        //     ()

        return failwith "elided: drain client.Events for human.input.request"
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
