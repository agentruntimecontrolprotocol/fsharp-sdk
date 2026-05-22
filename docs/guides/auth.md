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

`ArcpServerOptions.defaults` includes a permissive verifier that
accepts any non-empty token and maps it to a principal of `"dev"`. This
is useful for local testing but must never be used in production.

### Static verifier

`StaticBearerVerifier` accepts a map of `token → principal`:

```fsharp
open ARCP.Runtime

let verifier =
    StaticBearerVerifier(
        Map.ofList [
            ("token-alice", "alice")
            ("token-bob", "bob")
        ])

let options =
    { ArcpServerOptions.defaults with
        BearerVerifier = verifier }
let server = ArcpServer(options)
```

### Custom verifier

Implement `IBearerVerifier` to add JWT validation, database lookups, or
any other auth logic:

```fsharp
type JwtVerifier(signingKey: string) =
    interface IBearerVerifier with
        member _.VerifyAsync(token, ct) =
            task {
                let principal = validateJwt signingKey token
                return
                    if principal <> null then Some principal
                    else None
            }

let options =
    { ArcpServerOptions.defaults with
        BearerVerifier = JwtVerifier("my-secret") }
```

`VerifyAsync` should return `Some principal` for a valid token, or
`None` to reject. A rejected token causes the runtime to send
`session.error` with `UNAUTHENTICATED` and close the transport.

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

The spec reserves `x-vendor.<vendor>.<scheme>` for custom auth. To
accept vendor schemes, implement `IBearerVerifier` and inspect the
raw `AuthPayload.Scheme` field:

```fsharp
type MultiSchemeVerifier() =
    interface IBearerVerifier with
        member _.VerifyAsync(token, ct) =
            task {
                // 'token' is the raw Auth.Token value; scheme is inspected upstream
                return Some "any-principal"
            }
```

## See also

- [Sessions guide](sessions.md) — full handshake flow.
- [Spec §6.1](../../spec/docs/draft-arcp-1.1.md#61-authentication)
