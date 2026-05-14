module ArcpSamples.Resume

// Demonstrates the resume mechanism (§6.3). After a session ends,
// a new `session.hello` carrying a `resume` payload reattaches and
// replays events with `event_seq > last_event_seq`.

open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Client
open ARCP.Runtime
open ArcpSamples.SampleHarness

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let! p = connect (fun s -> s.RegisterAgent("noop", echoAgent)) Features.All
            match p.Client.Session with
            | Some s ->
                writeLine (sprintf "resume_token: %s" s.ResumeToken)
                writeLine (sprintf "resume_window_sec: %d" s.ResumeWindowSec)
            | None -> writeErr "no session"
            do! teardown p
            return 0
        })
