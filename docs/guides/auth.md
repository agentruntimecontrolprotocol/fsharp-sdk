# Auth (§6.1)

ARCP v1.x ships bearer-only auth. The token is carried in
`session.hello.payload.auth` and verified by the runtime before the
session is accepted.

## Client — sending a token

Pass `AuthScheme.Bearer` when constructing `ArcpClientOptions`:

```fsharp
open ARCP.Core
open ARCP.Client

let options =
    { ArcpClientOptions.defaults with
        Auth = AuthScheme.Bearer "my-token" }
```

For processes running inside a trust boundary (e.g. a local child
process started via stdio), use `AuthScheme.None`:

```fsharp
let options =
    { ArcpClientOptions.defaults with
        Auth = AuthScheme.None }
```

## Server — verifying tokens

### Dev-mode verifier (default)

`ArcpServerOptions.defaults` wires in `DevModeBearerVerifier`, which
accepts any non-empty token and maps it to a principal id of
`"dev:<token>"`. This is useful for local testing but must never be
used in production.

### Static verifier

`StaticBearerVerifier` accepts a `IReadOnlyDictionary<string, string>`
of `token → principal-id`:

```fsharp
open ARCP.Runtime.Auth

let verifier =
    StaticBearerVerifier(
        readOnlyDict [
            "token-alice", "alice"
            "token-bob",   "bob"
        ])

let options =
    { ArcpServerOptions.defaults with
        BearerVerifier = verifier }
let server = ArcpServer(options)
```

### Custom verifier

Implement `IBearerVerifier` to add JWT validation, database lookups, or
any other auth logic. `VerifyAsync` returns
`Task<Result<IPrincipal, ARCPError>>` — on rejection, supply the
`ARCPError.Unauthenticated` case you want surfaced:

```fsharp
open ARCP.Core
open ARCP.Runtime.Auth

type JwtVerifier(signingKey: string) =
    interface IBearerVerifier with
        member _.VerifyAsync(token, _ct) =
            task {
                match validateJwt signingKey token with
                | Some claims -> return Ok(StringPrincipal(claims.Subject) :> IPrincipal)
                | None -> return Error(ARCPError.Unauthenticated "Invalid bearer token")
            }

let options =
    { ArcpServerOptions.defaults with
        BearerVerifier = JwtVerifier("my-secret") }
```

Returning `Error` causes the runtime to send `session.error` with
`UNAUTHENTICATED` and close the transport. `StringPrincipal` lives in
`ARCP.Runtime.Auth`; you can also implement `IPrincipal` directly to
expose `Labels`.

## Wire shape

The auth payload on the wire is always:

```json
{ "scheme": "bearer", "token": "my-token" }
```

or for no-auth:

```json
{ "scheme": "none" }
```

The runtime ignores unrecognized schemes and maps them to
`UNAUTHENTICATED` — forward-compatibility for `x-vendor.*` auth
schemes requires explicit handling in a custom verifier.

## Vendor auth schemes

The spec reserves `x-vendor.<vendor>.<scheme>` for custom auth. The
current SDK only passes the bearer `token` to the verifier — `Auth`'s
raw `Scheme` field is consumed by the handshake before `VerifyAsync`
is called and is not surfaced here. To support vendor schemes today,
embed the scheme distinction inside the token itself and dispatch on
it from a custom verifier:

```fsharp
open ARCP.Core
open ARCP.Runtime.Auth

type MultiSchemeVerifier() =
    interface IBearerVerifier with
        member _.VerifyAsync(token, _ct) =
            task {
                if token.StartsWith("acme:") then
                    return Ok(StringPrincipal(token.Substring 5) :> IPrincipal)
                else
                    return Error(ARCPError.Unauthenticated "Unknown auth scheme")
            }
```

## See also

- [Sessions guide](sessions.md) — full handshake flow.
- [Spec §6.1](../../spec/docs/draft-arcp-1.1.md#61-authentication)
