namespace ARCP.Client

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open ARCP.Core

/// Bidirectional message transport over which ARCP envelopes flow.
///
/// Each `SendAsync` and `Receive` operates on raw `Envelope` values;
/// codec concerns live in `ARCP.Core.Codec`. Implementations are
/// expected to deliver envelopes in order and to surface transport
/// failure as `IAsyncEnumerable<_>` termination.
type ITransport =
    /// Send a single envelope. Completes when the transport has
    /// flushed it to the wire.
    abstract member SendAsync: envelope: Envelope * ct: CancellationToken -> Task

    /// Stream every received envelope. The enumerator terminates
    /// cleanly on graceful close and throws on fatal transport error.
    abstract member Receive: ct: CancellationToken -> IAsyncEnumerable<Envelope>

    /// Close the transport. Idempotent.
    abstract member CloseAsync: ct: CancellationToken -> Task
