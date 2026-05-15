module ARCP.UnitTests.AgentInventoryStoreTests

open System.Text.Json
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Runtime.Internal

let private noopHandler : AgentHandler =
    fun _ -> task { return JsonDocument.Parse("null").RootElement }

[<Fact>]
let ``Resolve with explicit version returns that version`` () =
    let inv = AgentInventoryStore()
    inv.Register("agent", "1.0.0", noopHandler)
    inv.Register("agent", "2.0.0", noopHandler)
    inv.SetDefault("agent", "2.0.0")
    match inv.Resolve "agent@1.0.0" with
    | Ok (_, "1.0.0", _) -> ()
    | other -> failwithf "got %A" other

[<Fact>]
let ``Resolve bare name returns default version`` () =
    let inv = AgentInventoryStore()
    inv.Register("agent", "1.0.0", noopHandler)
    inv.Register("agent", "2.0.0", noopHandler)
    inv.SetDefault("agent", "2.0.0")
    match inv.Resolve "agent" with
    | Ok (_, "2.0.0", _) -> ()
    | other -> failwithf "got %A" other

[<Fact>]
let ``Resolve unknown name returns AgentNotAvailable`` () =
    let inv = AgentInventoryStore()
    match inv.Resolve "ghost" with
    | Error (ARCPError.AgentNotAvailable "ghost") -> ()
    | other -> failwithf "got %A" other

[<Fact>]
let ``ToRichInventory returns one entry per registered name`` () =
    let inv = AgentInventoryStore()
    inv.Register("a", "1.0.0", noopHandler)
    inv.Register("b", "1.0.0", noopHandler)
    inv.ToRichInventory() |> List.length |> should equal 2

[<Fact>]
let ``ToFlatInventory returns just the names`` () =
    let inv = AgentInventoryStore()
    inv.Register("a", "1.0.0", noopHandler)
    inv.Register("b", "1.0.0", noopHandler)
    inv.ToFlatInventory() |> should equal [ "a"; "b" ]
