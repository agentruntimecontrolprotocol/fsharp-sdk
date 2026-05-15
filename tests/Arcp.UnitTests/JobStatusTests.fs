module ARCP.UnitTests.JobStatusTests

open Xunit
open FsUnit.Xunit
open ARCP.Core

[<Theory>]
[<InlineData("pending")>]
[<InlineData("running")>]
[<InlineData("success")>]
[<InlineData("error")>]
[<InlineData("cancelled")>]
[<InlineData("timed_out")>]
let ``ofWire then toWire is identity`` (wire: string) =
    let s = JobStatus.ofWire wire
    JobStatus.toWire s |> should equal wire

[<Fact>]
let ``tryOfWire rejects unknown wire string`` () =
    match JobStatus.tryOfWire "bogus" with
    | Error _ -> ()
    | Ok _ -> failwith "expected Error"

[<Fact>]
let ``ofWire throws on unknown wire string`` () =
    Assert.Throws<System.ArgumentException>(fun () ->
        JobStatus.ofWire "bogus" |> ignore)
    |> ignore
