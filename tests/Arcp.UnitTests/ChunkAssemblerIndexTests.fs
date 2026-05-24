module ARCP.UnitTests.ChunkAssemblerIndexTests

open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Client.Internal

[<Fact>]
let ``GetOrCreate returns the same assembler for repeated ids`` () =
    let idx = ChunkAssemblerIndex()
    let a = idx.GetOrCreate "rid-1"
    let b = idx.GetOrCreate "rid-1"
    obj.ReferenceEquals(a, b) |> should equal true

[<Fact>]
let ``GetOrCreate returns different assemblers for distinct ids`` () =
    let idx = ChunkAssemblerIndex()
    let a = idx.GetOrCreate "rid-1"
    let b = idx.GetOrCreate "rid-2"
    obj.ReferenceEquals(a, b) |> should equal false

[<Fact>]
let ``TryGet returns Some after GetOrCreate, None after Remove`` () =
    let idx = ChunkAssemblerIndex()
    idx.GetOrCreate "rid-1" |> ignore
    (idx.TryGet "rid-1").IsSome |> should equal true
    idx.Remove "rid-1" |> should equal true
    (idx.TryGet "rid-1").IsNone |> should equal true

[<Fact>]
let ``Remove returns false for unknown ids`` () =
    let idx = ChunkAssemblerIndex()
    idx.Remove "missing" |> should equal false

[<Fact>]
let ``ChunkAssembler rejects appends after stream closed`` () =
    let asm = ChunkAssembler()
    asm.Append(0L, "a", ChunkEncoding.Utf8, false) |> ignore

    match asm.Append(1L, "b", ChunkEncoding.Utf8, false) with
    | Error(ARCPError.InvalidRequest _) -> ()
    | other -> failwithf "expected InvalidRequest, got %A" other

[<Fact>]
let ``ChunkAssembler rejects invalid base64 with InvalidRequest`` () =
    let asm = ChunkAssembler()

    match asm.Append(0L, "!!!not-base64!!!", ChunkEncoding.Base64, false) with
    | Error(ARCPError.InvalidRequest _) -> ()
    | other -> failwithf "expected InvalidRequest, got %A" other

[<Fact>]
let ``ChunkAssembler ToArray throws before terminating chunk arrives`` () =
    let asm = ChunkAssembler()
    asm.Append(0L, "partial", ChunkEncoding.Utf8, true) |> ignore

    Assert.Throws<System.InvalidOperationException>(fun () -> asm.ToArray() |> ignore)
    |> ignore

[<Fact>]
let ``ChunkAssembler IsClosed flips true on terminator`` () =
    let asm = ChunkAssembler()
    asm.IsClosed |> should equal false
    asm.Append(0L, "x", ChunkEncoding.Utf8, false) |> ignore
    asm.IsClosed |> should equal true
