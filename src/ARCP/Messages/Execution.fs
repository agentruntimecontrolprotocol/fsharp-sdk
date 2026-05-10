namespace ARCP.Messages

open System
open System.Text.Json
open ARCP.Ids

/// <summary>
/// Execution payload records (RFC §10): tool invocations, durable jobs,
/// workflow/agent placeholders, and the shared <see cref="ErrorPayload"/>
/// shape for protocol-level error envelopes.
/// </summary>
module Execution =

    /// <summary>
    /// Reference to a content-addressed artifact (RFC §16). Defined here so
    /// it can be referenced from <see cref="ToolResult"/> and
    /// <see cref="JobCompleted"/>; <see cref="ARCP.Messages.Artifacts"/>
    /// re-exports it.
    /// </summary>
    type ArtifactRef =
        {
            ArtifactId: ArtifactId
            Uri: string
            MediaType: string
            Size: int64
            Sha256: string
            ExpiresAt: DateTimeOffset option
        }

    /// <summary>
    /// Flat wire-shape for a protocol error (RFC §18.1). Mirrors
    /// <see cref="ARCP.Errors.ARCPError"/> but in the JSON-friendly form
    /// expected on the wire by <c>tool.error</c>, <c>job.failed</c>, and
    /// <c>stream.error</c>.
    /// </summary>
    type ErrorPayload =
        {
            Code: string
            Message: string
            Retryable: bool option
            Details: JsonElement option
            Cause: JsonElement option
            TraceId: TraceId option
        }

    /// <summary><c>tool.invoke</c> payload (RFC §10.2).</summary>
    type ToolInvoke =
        { Tool: string; Arguments: JsonElement }

    /// <summary><c>tool.result</c> payload (RFC §10.2).</summary>
    type ToolResult =
        {
            Value: JsonElement option
            ResultRef: ArtifactRef option
        }

    /// <summary><c>tool.error</c> payload (RFC §10.2).</summary>
    type ToolError = ErrorPayload

    /// <summary><c>job.accepted</c> payload (RFC §10.3).</summary>
    type JobAccepted = { JobId: JobId }

    /// <summary><c>job.started</c> payload (RFC §10.3).</summary>
    type JobStarted = { JobId: JobId }

    /// <summary><c>job.progress</c> payload (RFC §10.3).</summary>
    type JobProgress =
        {
            Percent: int option
            Message: string option
        }

    /// <summary><c>job.heartbeat</c> payload (RFC §10.3).</summary>
    type JobHeartbeat =
        {
            Sequence: int
            DeadlineMs: int
            State: string
        }

    /// <summary><c>job.checkpoint</c> payload (RFC §10.7).</summary>
    type JobCheckpoint =
        {
            CheckpointId: string
            Data: JsonElement option
        }

    /// <summary><c>job.completed</c> payload (RFC §10.3).</summary>
    type JobCompleted =
        {
            Value: JsonElement option
            ResultRef: ArtifactRef option
        }

    /// <summary><c>job.failed</c> payload (RFC §10.3).</summary>
    type JobFailed = ErrorPayload

    /// <summary><c>job.cancelled</c> payload (RFC §10.4).</summary>
    type JobCancelled = { Reason: string option }

    // TODO(v0.2): §10.6 scheduled-jobs full schema; runtime returns Unimplemented.
    /// <summary><c>job.schedule</c> payload (RFC §10.6, placeholder).</summary>
    type JobSchedule = { When: JsonElement; Job: JsonElement }

    /// <summary><c>workflow.start</c> payload (RFC §10.8, placeholder).</summary>
    type WorkflowStart =
        {
            Workflow: string
            Arguments: JsonElement option
        }

    /// <summary><c>workflow.complete</c> payload (RFC §10.8, placeholder).</summary>
    type WorkflowComplete = { Value: JsonElement option }

    /// <summary><c>agent.delegate</c> payload (RFC §10.9, placeholder).</summary>
    type AgentDelegate =
        {
            Target: string
            Payload: JsonElement option
        }

    /// <summary><c>agent.handoff</c> payload (RFC §10.9, placeholder).</summary>
    type AgentHandoff =
        {
            Target: string
            Runtime: JsonElement option
        }
