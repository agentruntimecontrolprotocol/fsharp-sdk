namespace ARCP.Transport

open System

// TODO(phase 6): implement WebSocket transport (RFC §22).
/// <summary>Placeholder for the WebSocket transport; not yet implemented.</summary>
module WebSocket =
    /// <summary>Construct a WebSocket transport. Phase 2 stub: throws.</summary>
    let create () : ITransport =
        raise (NotSupportedException "websocket transport not implemented in phase 2")
