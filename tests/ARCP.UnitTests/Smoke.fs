module ARCP.UnitTests.Smoke

open Xunit
open FsUnit.Xunit

[<Fact>]
let ``protocol version is 1.0`` () =
    ARCP.Version.Protocol |> should equal "1.0"
