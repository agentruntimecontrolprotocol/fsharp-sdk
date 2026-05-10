namespace ARCP

open System
open System.Threading
open ARCP.Ids

/// <summary>
/// Distributed trace context (RFC §17.1). Flows across <c>task { ... }</c>
/// boundaries via <see cref="AsyncLocal{T}"/> so nested awaits inherit the
/// active trace without explicit threading.
/// </summary>
module Trace =

    /// <summary>
    /// A propagating trace context. <c>TraceId</c> is stable across an
    /// end-user request; <c>SpanId</c> identifies the current operation;
    /// <c>ParentSpanId</c> links to the caller in a trace tree.
    /// </summary>
    type TraceContext =
        { TraceId: TraceId
          SpanId: SpanId
          ParentSpanId: SpanId option }

    [<RequireQualifiedAccess>]
    module TraceContext =
        /// <summary>Start a new root context.</summary>
        let root () =
            { TraceId = TraceId.create ()
              SpanId = SpanId.create ()
              ParentSpanId = None }

        /// <summary>Derive a child span sharing the parent's trace id.</summary>
        let child (parent: TraceContext) =
            { TraceId = parent.TraceId
              SpanId = SpanId.create ()
              ParentSpanId = Some parent.SpanId }

    let private storage: AsyncLocal<TraceContext option> = AsyncLocal()

    /// <summary>The currently-active trace context, if any.</summary>
    let current () : TraceContext option = storage.Value

    /// <summary>
    /// Run <paramref name="action"/> with <paramref name="ctx"/> set as the
    /// active trace context. The previous context is restored on exit.
    /// </summary>
    let runWith (ctx: TraceContext) (action: unit -> 'T) : 'T =
        let previous = storage.Value
        storage.Value <- Some ctx

        try
            action ()
        finally
            storage.Value <- previous

    /// <summary>
    /// Run <paramref name="action"/> as a child span of the active context,
    /// or as a fresh root if no context is active.
    /// </summary>
    let inSpan (action: TraceContext -> 'T) : 'T =
        let ctx =
            match current () with
            | Some parent -> TraceContext.child parent
            | None -> TraceContext.root ()

        runWith ctx (fun () -> action ctx)
