namespace ARCP.Auth

open System.Collections.Generic
open ARCP.Errors
open ARCP.Auth.Auth

/// <summary>
/// Static-token <see cref="IAuthValidator"/> for the <c>bearer</c> scheme
/// (RFC §9.1). Tokens map directly to principal names.
/// </summary>
type BearerValidator(allowedTokens: IDictionary<string, string>) =
    interface IAuthValidator with
        member _.ValidateAsync(scheme, _ct) =
            task {
                match scheme with
                | Bearer token ->
                    match allowedTokens.TryGetValue token with
                    | true, principal ->
                        return
                            Ok
                                {
                                    Principal = principal
                                    ExpiresAt = None
                                }
                    | _ -> return Error(Unauthenticated "bearer token not recognized")
                | _ -> return Error(Unauthenticated "expected bearer scheme")
            }
