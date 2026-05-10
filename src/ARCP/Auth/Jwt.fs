namespace ARCP.Auth

open System
open Microsoft.IdentityModel.Tokens
open Microsoft.IdentityModel.JsonWebTokens
open ARCP.Errors
open ARCP.Auth.Auth

/// <summary>
/// JWT-validating <see cref="IAuthValidator"/> for the <c>signed_jwt</c>
/// scheme (RFC §9.1). Phase 2 only supports symmetric HS256 signing keys;
/// asymmetric (RS256/ES256) JWTs are deferred to later phases.
/// </summary>
type JwtValidator(issuerSigningKey: SecurityKey, validIssuer: string, validAudience: string) =
    let handler = JsonWebTokenHandler()

    let parameters =
        let p =
            TokenValidationParameters(
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = validIssuer,
                ValidAudience = validAudience,
                IssuerSigningKey = issuerSigningKey
            )

        p

    interface IAuthValidator with
        member _.ValidateAsync(scheme, _ct) =
            task {
                match scheme with
                | Jwt token ->
                    let! validation = handler.ValidateTokenAsync(token, parameters)

                    if validation.IsValid then
                        let principal =
                            match validation.Claims.TryGetValue "sub" with
                            | true, v -> string v
                            | _ -> "<unknown>"

                        let expiresAt =
                            match validation.Claims.TryGetValue "exp" with
                            | true, v ->
                                match v with
                                | :? int64 as i64 -> Some(DateTimeOffset.FromUnixTimeSeconds i64)
                                | :? int as i32 -> Some(DateTimeOffset.FromUnixTimeSeconds(int64 i32))
                                | _ -> None
                            | _ -> None

                        return
                            Ok
                                {
                                    Principal = principal
                                    ExpiresAt = expiresAt
                                }
                    else
                        return Error(Unauthenticated "jwt validation failed")
                | _ -> return Error(Unauthenticated "expected signed_jwt scheme")
            }
