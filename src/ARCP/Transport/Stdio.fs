namespace ARCP.Transport

open System

// TODO(phase 6): implement stdio newline-delimited JSON transport (RFC §22).
/// <summary>Placeholder for the stdio transport; not yet implemented.</summary>
module Stdio =
    /// <summary>Construct a stdio transport. Phase 2 stub: throws.</summary>
    let create () : ITransport =
        raise (NotSupportedException "stdio transport not implemented in phase 2")
