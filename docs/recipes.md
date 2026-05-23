# Recipes

Common patterns implemented in F#. Runnable versions live under
[`recipes/`](../recipes/).

## Graceful PERMISSION_DENIED from a tool call

Agents should treat a denied `tool.call` as a recoverable event, not
a fatal error. The runtime surfaces the denial as a
`tool_result` event with `error.code = "PERMISSION_DENIED"` on the job
event stream — the job stays alive.

```fsharp
server.RegisterAgent("strict", fun ctx ->
    task {
        do! ctx.EmitToolCallAsync(
                "send_reply",
                Json.serializeToElement<{| id: string |}> {| id = "m1" |},
                "call-1",
                ctx.CancellationToken)
        try
            do! ctx.ValidateOpAsync(Capabilities.ToolCall, "send_reply", ctx.CancellationToken)
            return Json.serializeToElement<{| sent: bool |}> {| sent = true |}
        with
        | :? ArcpException as ex ->
            do! ctx.EmitToolResultAsync(
                    "call-1",
                    ToolOutcome.Error(ex.Code, ex.Message, ex.Retryable),
                    ctx.CancellationToken)
            return Json.serializeToElement<{| sent: bool |}> {| sent = false |}
    })
```

See [`recipes/email-vendor-leases/`](../recipes/email-vendor-leases/).

## Multi-agent budget cascade

A planner agent submits child jobs and tracks their costs using
`EmitMetricAsync`. Each worker reports its own spend; the planner
checks `RemainingBudget` before launching more children.

```fsharp
server.RegisterAgent("planner", fun ctx ->
    task {
        match ctx.RemainingBudget |> Map.tryFind "USD" with
        | Some remaining when remaining >= 0.10m ->
            let childJob: DelegateBody = {
                ChildJobId = (JobId.newId()).Value
                Agent = "worker"
                Lease = Lease.empty |> Lease.withCapability Capabilities.CostBudget [ "USD:0.10" ]
                LeaseConstraints = None
            }
            do! ctx.EmitDelegateAsync(childJob, ctx.CancellationToken)
            do! ctx.EmitMetricAsync("cost.delegate", 0.10m, Some "USD", None, ctx.CancellationToken)
        | _ -> ()
        return Json.serializeToElement<{| delegated: bool |}> {| delegated = true |}
    })
```

See [`recipes/multi-agent-budget/`](../recipes/multi-agent-budget/).

## Chunked streaming + disconnect/resume

Large results are streamed as `result_chunk` events. If the client
disconnects mid-stream, it can resume the session and receive the
remaining chunks in order.

```fsharp
server.RegisterAgent("large-result", fun ctx ->
    task {
        let resultId = ctx.BeginStreamingResult()
        for i in 0L .. 99L do
            let bytes = Text.Encoding.UTF8.GetBytes(sprintf "chunk-%d" i)
            do! ctx.EmitResultChunkAsync(resultId, i, ReadOnlyMemory bytes, ChunkEncoding.Utf8, i < 99L, ctx.CancellationToken)
        return Json.serializeToElement<obj> null
    })
```

On the client side, `handle.Result` assembles all chunks automatically
before resolving.

See [`recipes/stream-resume/`](../recipes/stream-resume/).

## Vendor extension events

Custom event kinds use the `x-vendor.<vendor>.<kind>` namespace:

```fsharp
server.RegisterAgent("annotator", fun ctx ->
    task {
        // Emit a vendor-namespaced event
        do! ctx.EmitVendorEventAsync(
            "x-vendor.acme.confidence",
            Json.serializeToElement<{| score: float |}> {| score = 0.87 |},
            ctx.CancellationToken)
        return Json.serializeToElement<bool> true
    })
```

On the client, match the `XVendor` arm of `JobEventBody`:

```fsharp
let enumerator = handle.Events.GetAsyncEnumerator(ct)
let mutable more = true
while more do
    let! has = enumerator.MoveNextAsync().AsTask()
    if not has then more <- false
    else
        match enumerator.Current with
        | JobEventBody.XVendor("x-vendor.acme.confidence", body) -> printfn "%s" (body.GetRawText())
        | _ -> ()
```

See [`recipes/email-vendor-leases/`](../recipes/email-vendor-leases/)
and the [vendor extensions guide](guides/vendor-extensions.md).

## Idempotent retries

Combine a stable `IdempotencyKey` with a retry loop so duplicate
submits collapse to the same `job_id`:

```fsharp
let submitWithRetry (client: ArcpClient) (request: JobSubmitRequest) (ct: CancellationToken) =
    task {
        let mutable attempt = 0
        let mutable result : Result<JobResultPayload, ARCPError> option = None
        while result.IsNone && attempt < 3 do
            try
                let! handle = client.SubmitAsync(request, ct)
                let! r = handle.Result
                result <- Some r
            with
            | :? ArcpException as ex when ex.Retryable ->
                attempt <- attempt + 1
                do! Task.Delay(TimeSpan.FromSeconds(float (pown 2 attempt)), ct)
        return result.Value
    }
```

See [jobs guide](guides/jobs.md#idempotency).

## MCP skill bridge

Wrap an existing MCP tool as an ARCP agent so it's reachable over the
ARCP protocol:

```fsharp
server.RegisterAgent("mcp-bridge", fun ctx ->
    task {
        do! ctx.EmitStatusAsync("planning", Some "received MCP tool call", ctx.CancellationToken)
        return Json.serializeToElement<{| answer: string |}> {| answer = "ARCP job result for MCP" |}
    })
```

See [`recipes/mcp-skill/`](../recipes/mcp-skill/).
