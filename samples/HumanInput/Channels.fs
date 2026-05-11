/// Per-destination channel adapters. Real versions wrap ntfy.sh, SES, and
/// the Slack web API. Each returns a value matching the request's response_schema.
module ARCP.Samples.HumanInput.Channels

open System.Text.Json
open System.Threading
open System.Threading.Tasks

type ChannelResponse = string -> JsonElement -> CancellationToken -> Task<JsonElement>

let ntfyPhone: ChannelResponse =
    fun prompt schema ct -> task { return failwith "elided: ntfy.sh push" }

let emailOncall: ChannelResponse =
    fun prompt schema ct -> task { return failwith "elided: SES email" }

let slackOps: ChannelResponse =
    fun prompt schema ct -> task { return failwith "elided: slack web API" }

let registry: Map<string, ChannelResponse> =
    Map.ofList [ "ntfy:phone", ntfyPhone; "email:oncall", emailOncall; "slack:ops", slackOps ]
