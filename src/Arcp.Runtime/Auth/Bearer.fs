namespace ARCP.Runtime.Auth

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open ARCP.Core

/// Bearer verifier that accepts a static token → principal table.
/// Suitable for tests and the developer-mode CLI; production
/// deployments should plug their own `IBearerVerifier`. The table is
/// expected to be small (token comparison is linear and constant-time).
type StaticBearerVerifier(tokens: IReadOnlyDictionary<string, string>) =
    interface IBearerVerifier with
        member _.VerifyAsync(token, _ct) =
            task {
                // §119: compare with FixedTimeEquals so verification does
                // not short-circuit on the first differing character.
                let presented = Encoding.UTF8.GetBytes token

                let matched =
                    tokens
                    |> Seq.tryPick (fun kv ->
                        let candidate = Encoding.UTF8.GetBytes kv.Key

                        if CryptographicOperations.FixedTimeEquals(ReadOnlySpan<byte>(presented), ReadOnlySpan<byte>(candidate)) then
                            Some kv.Value
                        else
                            None)

                match matched with
                | Some principalId -> return Ok(StringPrincipal(principalId) :> IPrincipal)
                | None -> return Error(ARCPError.Unauthenticated "Invalid bearer token")
            }

/// Allows any non-empty token; useful for local development.
type DevModeBearerVerifier() =
    interface IBearerVerifier with
        member _.VerifyAsync(token, _ct) =
            task {
                // §54: reject whitespace-only tokens, not just null/empty.
                if System.String.IsNullOrWhiteSpace token then
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
