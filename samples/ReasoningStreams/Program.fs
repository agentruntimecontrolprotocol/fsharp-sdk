/// Primary emits reasoning; mirror peer subscribes, critiques back.
module ARCP.Samples.ReasoningStreams.Program

open System
open System.Text.Json
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control
open ARCP.Client
open ARCP.Envelope
open ARCP.Ids
open ARCP.Messages.Subscriptions
open ARCP.Samples.ReasoningStreams.Agents

let maxDepth = 3
let tokenBudget = 8_000

// Primary side -----------------------------------------------------------

let runPrimary (client: Client) (request: string) (inboundCritiques: Channel<Critique>) : Task<string> =
    task {
        let streamId = StreamId.create ()
        // client.OpenStreamAsync(streamId, kind = "thought")

        let mutable last: Critique option = None
        let mutable answer = ""
        let mutable halt = false
        let mutable step = 0

        while not halt && step < maxDepth do
            let! a = primaryStep request last
            answer <- a

            // client.SendStreamChunkAsync(streamId, sequence = step,
            //                             kind = "thought", role = "assistant_thought", content = answer)

            let timeoutCts =
                new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            try
                let! c = inboundCritiques.Reader.ReadAsync(timeoutCts.Token).AsTask()
                last <- Some c

                if c.Severity = Halt then
                    halt <- true
            with _ ->
                last <- None

            step <- step + 1

        return answer
    }

// Mirror side ------------------------------------------------------------
// A peer runtime, NOT a pure observer — it both reads the thought stream
// AND delegates critique events back.

let subscribeThoughts (mirror: Client) (targetSessionId: SessionId) : Task<SubscriptionId> =
    task {
        let filter: SubscribeFilter =
            {
                SessionIds = Some [ targetSessionId ]
                Types = [ "stream.chunk" ]
                JobIds = []
                StreamIds = []
                Roles = []
                MinPriority = None
            }

        match! mirror.SubscribeAsync filter with
        | Ok(sid, _) -> return sid
        | Error e -> return failwithf "subscribe failed: %A" e
    }

let isThought (env: Envelope<JsonElement>) : bool =
    env.Type = "stream.chunk"
    && (match env.Payload.TryGetProperty "kind" with
        | true, v -> v.GetString() = "thought"
        | _ -> false)

let runMirror (mirror: Client) (targetSessionId: SessionId) : Task =
    task {
        let! sid = subscribeThoughts mirror targetSessionId
        let mutable spent = 0
        let mutable running = true

        try
            // for env in mirror.Events do
            //   if isThought env then
            //     if spent >= tokenBudget then
            //       do! mirror.UnsubscribeAsync sid; running <- false
            //     else
            //       let! c = critiqueThought (env.Payload.GetProperty "content").GetString()
            //       spent <- spent + c.ConsumedTokens
            //       // delegate critique back into the primary's session via agent.delegate
            //       ()
            return failwith "elided: drain inbound, critique, delegate back"
        finally
            mirror.UnsubscribeAsync(sid).Wait()
    }
    :> Task

[<EntryPoint>]
let main _ =
    task {
        let primary: Client = Unchecked.defaultof<_> // transport, identity, auth elided
        let mirror: Client = Unchecked.defaultof<_>

        let inbound = Channel.CreateUnbounded<Critique>()

        let route =
            task {
                // for env in primary.Events do
                //   if env.Type = "agent.delegate" then
                //     let critique = parse env.Payload
                //     do! inbound.Writer.WriteAsync critique
                return ()
            }
            :> Task

        let _ = route
        let _ = runMirror mirror (SessionId.ofString "primary-session")

        let! answer = runPrimary primary "Argue both sides: serializable vs snapshot iso?" inbound

        printfn "%s" answer
        return ()
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
