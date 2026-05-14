module ARCP.UnitTests.AgentRefTests

open Xunit
open FsUnit.Xunit
open ARCP.Runtime.Internal

[<Fact>]
let ``bare name parses with no version`` () =
    let (name, version) = AgentRef.parse "code-refactor"
    name |> should equal "code-refactor"
    version |> should equal None

[<Fact>]
let ``name and version parses both parts`` () =
    let (name, version) = AgentRef.parse "code-refactor@2.0.0"
    name |> should equal "code-refactor"
    version |> should equal (Some "2.0.0")

[<Fact>]
let ``inventory rejects missing version`` () =
    let inv = AgentInventoryStore()
    inv.Register("agent", "1.0.0", fun _ -> task { return System.Text.Json.JsonDocument.Parse("null").RootElement })
    match inv.Resolve "agent@2.0.0" with
    | Error (ARCP.Core.ARCPError.AgentVersionNotAvailable("agent", "2.0.0")) -> ()
    | other -> failwithf "got %A" other

[<Fact>]
let ``inventory resolves default version`` () =
    let inv = AgentInventoryStore()
    inv.Register("agent", "1.0.0", fun _ -> task { return System.Text.Json.JsonDocument.Parse("null").RootElement })
    inv.Register("agent", "2.0.0", fun _ -> task { return System.Text.Json.JsonDocument.Parse("null").RootElement })
    inv.SetDefault("agent", "2.0.0")
    match inv.Resolve "agent" with
    | Ok (_, "2.0.0", _) -> ()
    | other -> failwithf "got %A" other
