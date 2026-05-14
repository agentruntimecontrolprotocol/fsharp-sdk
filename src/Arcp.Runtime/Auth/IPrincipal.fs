namespace ARCP.Runtime.Auth

open System.Threading
open System.Threading.Tasks
open ARCP.Core

/// Authenticated identity associated with a session.
///
/// The runtime carries the principal through every authority
/// decision: lease validation, list-jobs scope, subscription
/// authorization. Principal equality is by `Id`.
type IPrincipal =
    abstract member Id : string
    /// Free-form principal labels; useful for policy.
    abstract member Labels : Map<string, string>

/// Validates `session.hello.payload.auth`. Implementations decide
/// what counts as authentic; ARCP's contract is just "the principal
/// is whatever you return."
type IBearerVerifier =
    abstract member VerifyAsync :
        token: string * ct: CancellationToken -> Task<Result<IPrincipal, ARCPError>>

/// Anonymous principal used for `auth.scheme = "none"`.
type AnonymousPrincipal() =
    interface IPrincipal with
        member _.Id = "anonymous"
        member _.Labels = Map.empty

/// Simple principal that wraps an id string.
type StringPrincipal(id: string, labels: Map<string, string>) =
    new(id: string) = StringPrincipal(id, Map.empty)
    interface IPrincipal with
        member _.Id = id
        member _.Labels = labels
