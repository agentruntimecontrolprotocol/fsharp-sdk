namespace ARCP.Runtime.Auth

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open ARCP.Core

/// Bearer verifier that accepts a static token → principal table.
/// Suitable for tests and the developer-mode CLI; production
/// deployments should plug their own `IBearerVerifier`.
type StaticBearerVerifier(tokens: IReadOnlyDictionary<string, string>) =
    interface IBearerVerifier with
        member _.VerifyAsync(token, _ct) =
            task {
                match tokens.TryGetValue token with
                | true, principalId -> return Ok(StringPrincipal(principalId) :> IPrincipal)
                | _ -> return Error(ARCPError.Unauthenticated "Invalid bearer token")
            }

/// Allows any non-empty token; useful for local development.
type DevModeBearerVerifier() =
    interface IBearerVerifier with
        member _.VerifyAsync(token, _ct) =
            task {
                if System.String.IsNullOrEmpty token then
                    return Error(ARCPError.Unauthenticated "Missing bearer token")
                else
                    return Ok(StringPrincipal("dev:" + token) :> IPrincipal)
            }

/// Rejects every request — wired in when the runtime is configured
/// to require auth but no verifier is registered.
type AlwaysDenyVerifier() =
    interface IBearerVerifier with
        member _.VerifyAsync(_, _) =
            task { return Error(ARCPError.Unauthenticated "Authentication required") }
