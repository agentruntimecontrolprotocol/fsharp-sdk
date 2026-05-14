module ARCP.UnitTests.ChunkAssemblerTests

open System.Text
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Client.Internal

[<Fact>]
let ``in-order utf8 chunks assemble into the original string`` () =
    let asm = ChunkAssembler()
    asm.Append(0L, "hello ", ChunkEncoding.Utf8, true) |> ignore
    asm.Append(1L, "world", ChunkEncoding.Utf8, false) |> ignore
    Encoding.UTF8.GetString(asm.ToArray()) |> should equal "hello world"

[<Fact>]
let ``out-of-order chunk returns InvalidRequest`` () =
    let asm = ChunkAssembler()
    asm.Append(0L, "a", ChunkEncoding.Utf8, true) |> ignore
    match asm.Append(2L, "b", ChunkEncoding.Utf8, false) with
    | Error (ARCPError.InvalidRequest _) -> ()
    | other -> failwithf "expected InvalidRequest, got %A" other

[<Fact>]
let ``base64 chunks decode into raw bytes`` () =
    let asm = ChunkAssembler()
    let payload = [| 1uy; 2uy; 3uy |]
    asm.Append(0L, System.Convert.ToBase64String payload, ChunkEncoding.Base64, false) |> ignore
    asm.ToArray() |> should equal payload
