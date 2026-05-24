module ARCP.UnitTests.PropertyTests

open Xunit
open ARCP.Core

/// Table-driven equivalents of the property tests. FsCheck.Xunit v3
/// changed its attribute discovery — these are deterministic examples
/// that exercise the same invariants without the framework dependency.

[<Theory>]
[<InlineData "a">]
[<InlineData "msg-1234567890">]
[<InlineData "id_with_underscores">]
let ``MessageId round-trips through tryOfString`` (s: string) =
    match MessageId.tryOfString s with
    | Ok id -> Assert.Equal(s, id.Value)
    | Error e -> failwithf "expected Ok, got %s" e

[<Theory>]
[<InlineData "sess_x">]
[<InlineData "sess_long-form-id-123">]
let ``SessionId round-trips through ofString`` (s: string) =
    let id = SessionId.ofString s
    Assert.Equal(s, id.Value)
    Assert.Equal(s, id.ToString())

[<Theory>]
[<InlineData "job_x">]
[<InlineData "job_abc-def-ghi">]
let ``JobId round-trips through tryOfString`` (s: string) =
    match JobId.tryOfString s with
    | Ok id -> Assert.Equal(s, id.Value)
    | Error e -> failwithf "expected Ok, got %s" e

[<Theory>]
[<InlineData "res_x">]
[<InlineData "res_some-long-stream-id">]
let ``ResultId round-trips through ofString`` (s: string) =
    let id = ResultId.ofString s
    Assert.Equal(s, id.Value)
    Assert.Equal(s, id.ToString())

[<Theory>]
[<InlineData("s3://artifacts", "2026/page.html")>]
[<InlineData("/workspace/src", "main.fs")>]
[<InlineData("/data", "raw/x.txt")>]
let ``Glob ** matches anything under a literal prefix`` (prefix: string) (suffix: string) =
    Assert.True(Glob.isMatch (prefix + "/**") (prefix + "/" + suffix))

[<Theory>]
[<InlineData "exact">]
[<InlineData "render.png">]
[<InlineData "/workspace/main.fs">]
let ``Glob exact match always returns true`` (s: string) = Assert.True(Glob.isMatch s s)

[<Fact>]
let ``Lease.isSubset is reflexive on identical leases`` () =
    let lease = Lease.empty |> Lease.withCapability Capabilities.FsRead [ "/x/**" ]

    match Lease.isSubset lease lease Map.empty None None with
    | Ok() -> ()
    | Error e -> failwithf "expected Ok, got %A" e

[<Theory>]
[<InlineData(0)>]
[<InlineData(100)>]
[<InlineData(550)>]
[<InlineData(99999)>]
let ``Lease.parseBudgetAmount round-trips canonical strings`` (cents: int) =
    let v = decimal cents / 100m
    let amount = sprintf "USD:%O" v

    match Lease.parseBudgetAmount amount with
    | Ok(c, parsed) ->
        Assert.Equal("USD", c)
        Assert.Equal(v, parsed)
    | Error e -> failwithf "expected Ok, got %s" e

[<Fact>]
let ``ARCPError.code is non-empty for every error case`` () =
    let codes =
        [
            ARCPError.PermissionDenied("m", None)
            ARCPError.LeaseSubsetViolation("m", None)
            ARCPError.JobNotFound "j"
            ARCPError.DuplicateKey "k"
            ARCPError.AgentNotAvailable "a"
            ARCPError.AgentVersionNotAvailable("a", "v")
            ARCPError.Cancelled(Some "r")
            ARCPError.Cancelled None
            ARCPError.Timeout 1
            ARCPError.ResumeWindowExpired(0L, 1)
            ARCPError.HeartbeatLost
            ARCPError.LeaseExpired System.DateTimeOffset.UnixEpoch
            ARCPError.BudgetExhausted "USD"
            ARCPError.InvalidRequest("m", None)
            ARCPError.Unauthenticated "m"
            ARCPError.InternalError "m"
        ]

    for e in codes do
        Assert.False(System.String.IsNullOrEmpty(ARCPError.code e))

[<Theory>]
[<InlineData "msg-1">]
[<InlineData "id-with-dashes">]
[<InlineData "abcDEF123">]
let ``Envelope round-trips through Codec`` (msgId: string) =
    let payload = Json.parseElement "{\"a\":1}"

    let env = Envelope.create "session.hello" payload |> Envelope.withId msgId

    match Codec.writeEnvelope env |> Codec.readEnvelope with
    | Ok back -> Assert.Equal(msgId, back.Id)
    | Error e -> failwithf "expected Ok, got %A" e
