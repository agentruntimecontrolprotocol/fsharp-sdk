module ArcpSamples.Heartbeat

// Demonstrates `heartbeat` (§6.4): `session.welcome` carries
// `heartbeat_interval_sec`, and `session.ping`/`session.pong`
// flow on idle. The client auto-pongs every received ping.

open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Runtime
open ArcpSamples.SampleHarness

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let! p = connect (fun s -> s.RegisterAgent("noop", echoAgent)) (Set.singleton Features.Heartbeat)

            match p.Client.Session with
            | Some s ->
                writeLine (sprintf "negotiated: %A" (s.NegotiatedFeatures |> Set.toList))
                writeLine (sprintf "heartbeat_interval_sec: %A" s.HeartbeatIntervalSec)
            | None -> writeErr "no session"

            do! teardown p
            return 0
        })
