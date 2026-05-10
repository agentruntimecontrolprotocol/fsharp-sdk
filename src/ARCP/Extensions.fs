namespace ARCP

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open ARCP.Errors

/// <summary>
/// Extension namespace validation and unknown-message handling (RFC §21).
/// Extension types must use one of:
/// <list type="bullet">
///   <item><c>arcpx.&lt;vendor-or-domain&gt;.&lt;name&gt;.v&lt;n&gt;</c></item>
///   <item>Reverse-DNS (e.g. <c>com.acme.workflow.v2</c>)</item>
/// </list>
/// The bare <c>x-</c> prefix is reserved for transport-internal experimental
/// fields and must not appear in long-lived deployments.
/// </summary>
module Extensions =

    /// <summary>Core message-type prefixes a runtime is required to support.</summary>
    let coreTypePrefixes =
        [
            "session."
            "ping"
            "pong"
            "ack"
            "nack"
            "cancel"
            "interrupt"
            "resume"
            "backpressure"
            "checkpoint."
            "tool."
            "job."
            "workflow."
            "agent."
            "stream."
            "human."
            "permission."
            "lease."
            "subscribe"
            "unsubscribe"
            "artifact."
            "event.emit"
            "log"
            "metric"
            "trace.span"
        ]

    let private arcpxPattern =
        Regex(@"^arcpx\.[a-z0-9][a-z0-9_-]*(\.[a-z0-9][a-z0-9_-]*)+\.v\d+$", RegexOptions.Compiled)

    let private reverseDnsPattern =
        Regex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9_-]*)+\.v\d+$", RegexOptions.Compiled)

    /// <summary>True if a wire type string is a recognized core message type.</summary>
    let isCoreType (envType: string) : bool =
        if String.IsNullOrEmpty envType then
            false
        else
            coreTypePrefixes
            |> List.exists (fun p ->
                if p.EndsWith "." then
                    envType.StartsWith p
                elif p.Contains "." then
                    envType = p || envType.StartsWith(p + ".")
                else
                    envType = p)

    /// <summary>True if a wire type matches the namespaced extension grammar (§21.1).</summary>
    let isExtensionType (envType: string) : bool =
        if String.IsNullOrEmpty envType then
            false
        elif envType.StartsWith "x-" then
            false
        else
            arcpxPattern.IsMatch envType || reverseDnsPattern.IsMatch envType

    /// <summary>
    /// Validate an extension namespace string. Returns <c>Ok ()</c> if it
    /// matches one of the accepted forms; otherwise <c>InvalidArgument</c>.
    /// </summary>
    let validateNamespace (envType: string) : Result<unit, ARCPError> =
        if isExtensionType envType then
            Ok()
        else
            Error(InvalidArgument("type", sprintf "%s is not a valid extension namespace (RFC §21.1)" envType))

    /// <summary>Decision for an unknown message type per RFC §21.3.</summary>
    type UnknownDisposition =
        /// <summary>Reject with <c>nack</c> + <c>UNIMPLEMENTED</c>.</summary>
        | Reject of ARCPError
        /// <summary>Silently drop (sender opted into <c>extensions.optional: true</c>).</summary>
        | Drop

    /// <summary>
    /// Decide what to do with an unknown message type given its wire type and
    /// the parsed <c>extensions</c> object from the envelope.
    /// </summary>
    let decide (envType: string) (extensions: JsonObject option) : UnknownDisposition =
        if isCoreType envType then
            Reject(Unimplemented(sprintf "core type %s not implemented by this runtime" envType))
        elif isExtensionType envType then
            let optional =
                match extensions with
                | Some ext ->
                    let mutable node: JsonNode = null

                    if ext.TryGetPropertyValue("optional", &node) && not (isNull node) then
                        match node with
                        | :? JsonValue as v ->
                            match v.TryGetValue<bool>() with
                            | true, b -> b
                            | _ -> false
                        | _ -> false
                    else
                        false
                | None -> false

            if optional then
                Drop
            else
                Reject(Unimplemented(sprintf "extension %s not advertised" envType))
        else
            Reject(InvalidArgument("type", sprintf "%s is neither core nor a recognized extension" envType))
