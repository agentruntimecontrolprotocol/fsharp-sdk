namespace ARCP.Messages

open System.Text.Json
open ARCP.Ids

/// <summary>
/// Control-plane payload records (RFC §6, §7, §10.4). Includes liveness
/// (<c>ping</c>/<c>pong</c>), acknowledgement (<c>ack</c>/<c>nack</c>),
/// cancellation, interrupt/resume, backpressure, and checkpoint controls.
/// </summary>
module Control =

    /// <summary><c>ping</c> payload (RFC §7.1).</summary>
    type Ping = { Nonce: string option }

    /// <summary><c>pong</c> payload (RFC §7.1).</summary>
    type Pong = { Nonce: string option }

    /// <summary><c>ack</c> payload (RFC §6.3).</summary>
    type Ack = { Message: string option }

    /// <summary><c>nack</c> payload (RFC §6.3, §18).</summary>
    type Nack =
        {
            Code: string
            Message: string
            Details: JsonElement option
        }

    /// <summary><c>cancel</c> payload (RFC §10.4).</summary>
    type Cancel =
        {
            Target: string
            TargetId: string
            Reason: string option
            DeadlineMs: int option
        }

    /// <summary><c>cancel.accepted</c> payload (RFC §10.4).</summary>
    type CancelAccepted = { Target: string; TargetId: string }

    /// <summary><c>cancel.refused</c> payload (RFC §10.4).</summary>
    type CancelRefused =
        {
            Target: string
            TargetId: string
            Reason: string option
        }

    /// <summary><c>interrupt</c> payload (RFC §10.5).</summary>
    type Interrupt =
        {
            JobId: JobId option
            Reason: string option
        }

    /// <summary><c>resume</c> payload (RFC §19).</summary>
    type Resume =
        {
            AfterMessageId: MessageId option
            CheckpointId: string option
            IncludeOpenStreams: bool option
        }

    /// <summary><c>backpressure</c> payload (RFC §12).</summary>
    type Backpressure =
        {
            DesiredRatePerSecond: int option
            BufferRemainingBytes: int64 option
            Reason: string option
        }

    /// <summary><c>checkpoint.create</c> payload (RFC §10.7).</summary>
    type CheckpointCreate = { JobId: JobId; Label: string option }

    /// <summary><c>checkpoint.restore</c> payload (RFC §10.7).</summary>
    type CheckpointRestore = { JobId: JobId; CheckpointId: string }
