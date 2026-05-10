module ARCP.UnitTests.RegistryTests

open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open ARCP
open ARCP.Errors
open ARCP.Ids
open ARCP.Messages.Session
open ARCP.Messages.Registry

let private allWireSamples: MessageType list =
    let dummy = JsonDocument.Parse("{}").RootElement

    [
        SessionOpen
            {
                Arcp = Version.Protocol
                Client =
                    {
                        Kind = "k"
                        Version = "v"
                        Fingerprint = None
                        Principal = None
                    }
                Auth =
                    {
                        Scheme = "none"
                        Token = None
                        Fingerprint = None
                    }
                Capabilities = Capabilities.empty
            }
        SessionChallenge
            {
                Scheme = "bearer"
                Challenge = "x"
                ExpiresAt = None
            }
        SessionAuthenticate
            {
                Scheme = "bearer"
                Token = "t"
                Challenge = None
            }
        SessionAccepted
            {
                SessionId = SessionId "s"
                Runtime =
                    {
                        Kind = "k"
                        Version = "v"
                        Fingerprint = None
                        TrustLevel = None
                    }
                Capabilities = Capabilities.empty
                Lease = None
            }
        SessionUnauthenticated { Code = "X"; Reason = None }
        SessionRejected { Code = "X"; Reason = None }
        SessionRefresh
            {
                Scheme = "bearer"
                Challenge = "x"
                DeadlineMs = None
            }
        SessionEvicted { Code = "X"; Reason = None }
        SessionClose { Reason = None }
    ]

[<Fact>]
let ``wireType strings are unique for sampled cases`` () =
    let strings = allWireSamples |> List.map wireType
    Assert.Equal(strings.Length, List.distinct strings |> List.length)

[<Fact>]
let ``ofWireType inverts wireType for known core types`` () =
    for msg in allWireSamples do
        let t = wireType msg
        let payload = toPayloadElement msg

        match ofWireType t payload with
        | Ok decoded -> Assert.Equal(t, wireType decoded)
        | Error e -> failwithf "ofWireType %s failed: %A" t e

[<Fact>]
let ``unknown core-prefixed type yields Unimplemented`` () =
    let payload = JsonDocument.Parse("{}").RootElement

    match ofWireType "session.bogus" payload with
    | Error(Unimplemented _) -> ()
    | other -> failwithf "expected Unimplemented, got %A" other

[<Fact>]
let ``namespaced extension type yields Extension`` () =
    let payload = JsonDocument.Parse("{\"k\":1}").RootElement

    match ofWireType "arcpx.acme.foo.v1" payload with
    | Ok(Extension e) -> Assert.Equal("arcpx.acme.foo.v1", e.Type)
    | other -> failwithf "expected Extension, got %A" other

[<Fact>]
let ``invalid type string yields InvalidArgument`` () =
    let payload = JsonDocument.Parse("{}").RootElement

    match ofWireType "x-not-namespaced" payload with
    | Error(InvalidArgument _) -> ()
    | other -> failwithf "expected InvalidArgument, got %A" other
