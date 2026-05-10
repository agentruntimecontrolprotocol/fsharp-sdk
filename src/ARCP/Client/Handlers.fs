namespace ARCP.Client

open System.Threading
open System.Threading.Tasks
open ARCP.Errors
open ARCP.Messages.Human
open ARCP.Messages.Permissions

/// <summary>Callback for runtime-issued <c>human.input.request</c> events (RFC §14).</summary>
type IHumanInputHandler =
    /// <summary>Produce a response value or an error.</summary>
    abstract HandleAsync:
        request: HumanInputRequest * ct: CancellationToken -> Task<Result<HumanInputResponse, ARCPError>>

/// <summary>Callback for runtime-issued <c>permission.request</c> events (RFC §15).</summary>
type IPermissionHandler =
    /// <summary>Decide whether to grant or deny the permission.</summary>
    abstract HandleAsync: request: PermissionRequest * ct: CancellationToken -> Task<Result<PermissionGrant, ARCPError>>

/// <summary>
/// Default human-input handler. Returns the request's <c>default</c> value if
/// present; otherwise <see cref="PermissionDenied"/>.
/// </summary>
type DefaultHumanInputHandler() =
    interface IHumanInputHandler with
        member _.HandleAsync(request, _ct) =
            task {
                match request.Default with
                | Some d ->
                    return
                        Ok
                            {
                                Value = d
                                RespondedBy = Some "default"
                                RespondedAt = Some(System.DateTimeOffset.UtcNow)
                            }
                | None -> return Error(PermissionDenied("human.input", "no default response"))
            }
