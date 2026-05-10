namespace ARCP.Messages

open System
open System.Text.Json

/// <summary>Human-in-the-loop payload records (RFC §14).</summary>
module Human =

    /// <summary><c>human.input.request</c> payload (RFC §14.1).</summary>
    type HumanInputRequest =
        {
            Prompt: string
            ResponseSchema: JsonElement option
            Default: JsonElement option
            ExpiresAt: DateTimeOffset
        }

    /// <summary><c>human.input.response</c> payload (RFC §14.1).</summary>
    type HumanInputResponse =
        {
            Value: JsonElement
            RespondedBy: string option
            RespondedAt: DateTimeOffset option
        }

    /// <summary>One option in a multi-choice picker (RFC §14.2).</summary>
    type ChoiceOption = { Id: string; Label: string }

    /// <summary><c>human.choice.request</c> payload (RFC §14.2).</summary>
    type HumanChoiceRequest =
        {
            Prompt: string
            Options: ChoiceOption list
            ExpiresAt: DateTimeOffset
        }

    /// <summary><c>human.choice.response</c> payload (RFC §14.2).</summary>
    type HumanChoiceResponse =
        {
            ChoiceId: string
            RespondedBy: string option
            RespondedAt: DateTimeOffset option
        }

    /// <summary><c>human.input.cancelled</c> payload (RFC §14.3).</summary>
    type HumanInputCancelled = { Code: string; Reason: string option }
