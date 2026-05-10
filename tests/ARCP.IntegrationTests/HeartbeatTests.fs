module ARCP.IntegrationTests.HeartbeatTests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Microsoft.Extensions.Time.Testing
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Envelope
open ARCP.Messages.Registry
open ARCP.Runtime

[<Fact>]
let ``external job: missed heartbeats -> HEARTBEAT_LOST`` () =
    task {
        let fake = FakeTimeProvider()
        fake.SetUtcNow(DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))

        let captured =
            System.Collections.Concurrent.ConcurrentQueue<Envelope<MessageType>>()

        let send env =
            captured.Enqueue env
            Task.CompletedTask

        let interval = TimeSpan.FromSeconds 1.0

        let mgr =
            JobManager(fake :> TimeProvider, None, interval, missedDeadlineLimit = 2, send = send)

        let sid = SessionId.create ()

        let run =
            fun (ct: CancellationToken) ->
                task {
                    try
                        do! Task.Delay(TimeSpan.FromHours 1.0, ct)
                        return Ok(JsonSerializer.SerializeToElement<int>(0))
                    with _ ->
                        return Error(Cancelled "cancelled")
                }

        let! jid = mgr.AcceptAsync(sid, "external", run, origin = Job.External)

        // Let watchdog spin up
        do! Task.Delay(50)

        // Record one heartbeat; advance time by interval; no transition expected.
        do! mgr.RecordHeartbeatAsync(jid, 1, 5000)
        fake.Advance interval
        do! Task.Delay(50)

        match mgr.TryGetState jid with
        | Some(Job.Running)
        | Some(Job.Accepted) -> ()
        | other -> failwithf "expected Running/Accepted after one beat, got %A" other

        // Now skip heartbeats: advance multiple intervals without recording.
        for _ in 1..4 do
            fake.Advance interval
            do! Task.Delay(50)

        // Give the watchdog a moment to observe the lapse and emit Failed.
        do! Task.Delay(200)

        match mgr.TryGetState jid with
        | Some(Job.Failed(HeartbeatLost _)) -> ()
        | other -> failwithf "expected Failed HeartbeatLost, got %A" other

        let sawJobFailed = captured |> Seq.exists (fun e -> e.Type = "job.failed")

        Assert.True(sawJobFailed, "expected a job.failed envelope")
    }
