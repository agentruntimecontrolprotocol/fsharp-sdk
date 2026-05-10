namespace ARCP.Auth

open System
open System.Threading
open System.Threading.Tasks
open ARCP.Errors

/// <summary>
/// Authentication primitives for the session handshake (RFC §9.1). Phase 2
/// supports three schemes only: <c>bearer</c>, <c>signed_jwt</c>, and
/// <c>none</c>. Other schemes are reserved for later phases.
/// </summary>
module Auth =

    /// <summary>Supported authentication scheme.</summary>
    type AuthScheme =
        /// <summary>RFC §9.1 <c>bearer</c>: opaque token compared by equality.</summary>
        | Bearer of token: string
        /// <summary>RFC §9.1 <c>signed_jwt</c>: HS256-signed JSON Web Token.</summary>
        | Jwt of token: string
        /// <summary>RFC §9.1 <c>none</c>: anonymous; only honored if the runtime advertises <c>anonymous: true</c>.</summary>
        | Anonymous

    [<RequireQualifiedAccess>]
    module AuthScheme =
        /// <summary>The canonical wire string for an <see cref="AuthScheme"/>.</summary>
        let wire =
            function
            | Bearer _ -> "bearer"
            | Jwt _ -> "signed_jwt"
            | Anonymous -> "none"

    /// <summary>Successful authentication outcome.</summary>
    type AuthResult =
        {
            Principal: string
            ExpiresAt: DateTimeOffset option
        }

    /// <summary>Pluggable credential validator (RFC §9.1).</summary>
    type IAuthValidator =
        /// <summary>Validate a credential. <c>Ok</c> establishes the principal; <c>Error</c> rejects.</summary>
        abstract ValidateAsync: scheme: AuthScheme * ct: CancellationToken -> Task<Result<AuthResult, ARCPError>>

    /// <summary>
    /// Validator that accepts <see cref="AuthScheme.None"/> as the
    /// <c>anonymous</c> principal. The runtime, not this validator,
    /// gates whether anonymous sessions are permitted.
    /// </summary>
    type NullValidator() =
        interface IAuthValidator with
            member _.ValidateAsync(scheme, _ct) =
                task {
                    match scheme with
                    | Anonymous ->
                        return
                            Ok
                                {
                                    Principal = "anonymous"
                                    ExpiresAt = None
                                }
                    | _ -> return Error(Unauthenticated "scheme not supported by NullValidator")
                }
