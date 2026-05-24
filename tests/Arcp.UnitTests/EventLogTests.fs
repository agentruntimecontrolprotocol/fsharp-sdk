module ARCP.UnitTests.EventLogTests

open System
open Microsoft.Extensions.Time.Testing
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime.Store

let private mkEnv id =
    // EventLog doesn't decode payloads — any valid JsonElement works.
    Envelope.create "job.event" (Json.parseElement "{}") |> Envelope.withId id

let private mkLog (fake: FakeTimeProvider) (maxPerSession: int) (resumeWindowSec: int) =
    EventLog(
        { EventLogOptions.defaults with
            MaxPerSession = maxPerSession
            ResumeWindowSec = resumeWindowSec
            TimeProvider = fake
        }
    )

[<Fact>]
let ``NextSeq increments per session monotonically`` () =
    let log = mkLog (FakeTimeProvider()) 100 600
    let sid = SessionId.newId ()
    log.NextSeq sid |> should equal 1L
    log.NextSeq sid |> should equal 2L
    log.NextSeq sid |> should equal 3L
    log.CurrentSeq sid |> should equal 3L

[<Fact>]
let ``CurrentSeq for unknown session is zero`` () =
    let log = mkLog (FakeTimeProvider()) 100 600
    log.CurrentSeq(SessionId.newId ()) |> should equal 0L

[<Fact>]
let ``Append assigns a seq, returns ordered entries`` () =
    let log = mkLog (FakeTimeProvider()) 100 600
    let sid = SessionId.newId ()
    let a = log.Append(sid, mkEnv "a")
    let b = log.Append(sid, mkEnv "b")
    a.EventSeq |> should equal 1L
    b.EventSeq |> should equal 2L
    log.All sid |> Seq.length |> should equal 2

[<Fact>]
let ``Append over MaxPerSession evicts the oldest entry`` () =
    let log = mkLog (FakeTimeProvider()) 2 600
    let sid = SessionId.newId ()
    log.Append(sid, mkEnv "a") |> ignore
    log.Append(sid, mkEnv "b") |> ignore
    log.Append(sid, mkEnv "c") |> ignore
    let snapshot = log.All sid |> Seq.toArray
    snapshot.Length |> should equal 2
    snapshot.[0].EventSeq |> should equal 2L
    snapshot.[1].EventSeq |> should equal 3L

[<Fact>]
let ``Replay returns events after fromSeq`` () =
    let log = mkLog (FakeTimeProvider()) 100 600
    let sid = SessionId.newId ()
    log.Append(sid, mkEnv "a") |> ignore
    log.Append(sid, mkEnv "b") |> ignore
    log.Append(sid, mkEnv "c") |> ignore

    match log.Replay(sid, 1L) with
    | Ok xs ->
        let arr = xs |> Seq.toArray
        arr.Length |> should equal 2
        arr.[0].EventSeq |> should equal 2L
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``Replay returns empty for unknown session`` () =
    let log = mkLog (FakeTimeProvider()) 100 600

    match log.Replay(SessionId.newId (), 0L) with
    | Ok xs -> xs |> Seq.isEmpty |> should equal true
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``Replay returns RESUME_WINDOW_EXPIRED when fromSeq is too old`` () =
    let log = mkLog (FakeTimeProvider()) 2 600
    let sid = SessionId.newId ()
    log.Append(sid, mkEnv "a") |> ignore
    log.Append(sid, mkEnv "b") |> ignore
    log.Append(sid, mkEnv "c") |> ignore

    match log.Replay(sid, 0L) with
    | Error(ARCPError.ResumeWindowExpired _) -> ()
    | other -> failwithf "expected ResumeWindowExpired, got %A" other

[<Fact>]
let ``EvictExpired removes only entries older than resume window`` () =
    let fake = FakeTimeProvider()
    fake.SetUtcNow(DateTimeOffset.Parse "2026-01-01T00:00:00Z")
    let log = mkLog fake 100 60
    let sid = SessionId.newId ()
    log.Append(sid, mkEnv "a") |> ignore
    fake.Advance(TimeSpan.FromSeconds 30.0)
    log.Append(sid, mkEnv "b") |> ignore
    fake.Advance(TimeSpan.FromSeconds 40.0)
    // a is 70s old, b is 40s old. Window is 60s → only a evicted.
    let removed = log.EvictExpired()
    removed |> should equal 1
    let snapshot = log.All sid |> Seq.toArray
    snapshot.Length |> should equal 1
    snapshot.[0].EventSeq |> should equal 2L

[<Fact>]
let ``EvictExpired removes many entries efficiently`` () =
    let fake = FakeTimeProvider()
    fake.SetUtcNow(DateTimeOffset.Parse "2026-01-01T00:00:00Z")
    let log = mkLog fake 10_000 60
    let sid = SessionId.newId ()

    for i in 1..1000 do
        log.Append(sid, mkEnv (sprintf "e%d" i)) |> ignore

    fake.Advance(TimeSpan.FromSeconds 120.0)
    let removed = log.EvictExpired()
    removed |> should equal 1000
    log.All sid |> Seq.length |> should equal 0

[<Fact>]
let ``Drop forgets the session entirely`` () =
    let log = mkLog (FakeTimeProvider()) 100 600
    let sid = SessionId.newId ()
    log.Append(sid, mkEnv "a") |> ignore
    log.Drop sid
    log.All sid |> Seq.length |> should equal 0
    log.CurrentSeq sid |> should equal 0L
