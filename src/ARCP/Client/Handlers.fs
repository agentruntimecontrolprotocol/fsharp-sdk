namespace ARCP.Client

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ARCP.Errors
open ARCP.Messages.Human

/// <summary>
/// Permission decision returned by an <see cref="IPermissionHandler"/>
/// (RFC §15.4).
/// </summary>
type PermissionDecision =
    /// <summary>Grant the permission, optionally with a lease lifetime.</summary>
    | Grant of leaseSeconds: int option
    /// <summary>Deny with an optional reason string.</summary>
    | Deny of reason: string option

/// <summary>
/// Client-side handler for <c>human.input.request</c> envelopes (RFC §14.1).
/// </summary>
type IHumanInputHandler =
    /// <summary>Produce a JSON-encoded response for the runtime prompt.</summary>
    abstract HandleAsync:
        prompt: string *
        schema: JsonElement option *
        dflt: JsonElement option *
        expiresAt: DateTimeOffset *
        ct: CancellationToken ->
            Task<JsonElement>

/// <summary>
/// Client-side handler for <c>human.choice.request</c> envelopes (RFC §14.2).
/// </summary>
type IChoiceHandler =
    /// <summary>Pick one of the offered choice ids.</summary>
    abstract HandleAsync:
        prompt: string * options: ChoiceOption list * expiresAt: DateTimeOffset * ct: CancellationToken -> Task<string>

/// <summary>
/// Client-side handler for <c>permission.request</c> envelopes (RFC §15.4).
/// </summary>
type IPermissionHandler =
    /// <summary>Decide whether to grant or deny the request.</summary>
    abstract HandleAsync:
        permission: string *
        resource: string *
        operation: string *
        reason: string option *
        leaseSeconds: int option *
        ct: CancellationToken ->
            Task<PermissionDecision>

/// <summary>
/// Default human-input handler: returns the runtime-provided default if
/// present, otherwise throws (so callers know they must register a real
/// handler).
/// </summary>
type DefaultHumanInputHandler() =
    interface IHumanInputHandler with
        member _.HandleAsync(_prompt, _schema, dflt, _expiresAt, _ct) =
            task {
                match dflt with
                | Some d -> return d
                | None -> return raise (InvalidOperationException "no default and no human-input handler registered")
            }

/// <summary>
/// Permission handler that grants every request with the requested lease
/// lifetime (or unbounded if none was requested). Useful for tests.
/// </summary>
type AlwaysAllowPermissionHandler() =
    interface IPermissionHandler with
        member _.HandleAsync(_permission, _resource, _operation, _reason, leaseSeconds, _ct) =
            task { return Grant leaseSeconds }
