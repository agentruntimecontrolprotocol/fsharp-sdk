module ARCP.UnitTests.AutoAckSchedulerTests

open System
open Microsoft.Extensions.Time.Testing
open Xunit
open FsUnit.Xunit
open ARCP.Client.Internal

[<Fact>]
let ``OnEvent flushes at the EveryEvents threshold`` () =
    let fake = FakeTimeProvider()
    fake.SetUtcNow(DateTimeOffset.UtcNow)
    let sched = AutoAckScheduler(
                    { EveryEvents = 4; Interval = TimeSpan.FromMinutes 1.0 },
                    fake)
    sched.OnEvent 1L |> should equal None
    sched.OnEvent 2L |> should equal None
    sched.OnEvent 3L |> should equal None
    sched.OnEvent 4L |> should equal (Some 4L)

[<Fact>]
let ``OnEvent flushes when the interval elapses`` () =
    let fake = FakeTimeProvider()
    fake.SetUtcNow(DateTimeOffset.UtcNow)
    let sched = AutoAckScheduler(
                    { EveryEvents = 100; Interval = TimeSpan.FromMilliseconds 250.0 },
                    fake)
    sched.OnEvent 1L |> should equal None
    fake.Advance(TimeSpan.FromMilliseconds 300.0)
    sched.OnEvent 2L |> should equal (Some 2L)
