module ARCP.IntegrationTests.Helpers

open Xunit

[<Fact>]
let ``integration test project compiles and discovers tests`` () =
    Assert.Equal("1.0", ARCP.Version.Protocol)
