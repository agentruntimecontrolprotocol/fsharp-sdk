module ARCP.Samples.PermissionChallenge.Program

open System
open System.Text.Json
open System.Threading
open Microsoft.Extensions.Logging.Abstractions
open ARCP.Ids
open ARCP.Messages.Session
open ARCP.Auth
open ARCP.Auth.Auth
open ARCP.Transport
open ARCP.Runtime
open ARCP.Client

let private jsonString (s: string) = JsonSerializer.SerializeToElement<string>(s)

/// <summary>
/// Sample 04 — a tool calls <c>ctx.RequestPermissionAsync</c> (RFC §15.4).
/// The client grants via <see cref="AlwaysAllowPermissionHandler"/>; the
/// runtime allocates a lease and the tool returns its id and expiry.
/// </summary>
[<EntryPoint>]
let main _argv =
    task {
        let serverT, clientT = Memory.createPair ()
        let tokens = dict [ "secret", "alice" ]
        let validator = BearerValidator tokens :> IAuthValidator

        let opts =
            { RuntimeOptions.defaults with
                OfferedCapabilities = Capabilities.empty
            }

        let runtime = new Runtime(serverT, validator, NullLogger.Instance, opts)
        let _ = runtime.StartAsync CancellationToken.None

        runtime.RegisterTool(
            "refund",
            fun (ctx: ToolContext) _ ->
                task {
                    let! r =
                        ctx.RequestPermissionAsync(
                            ("payment.refund.create",
                             "order:42",
                             "refund",
                             Some "customer requested",
                             Some 120,
                             ctx.CancellationToken)
                        )

                    match r with
                    | Ok lease ->
                        let payload =
                            sprintf "lease=%s expires=%s" (LeaseId.value lease.LeaseId) (lease.ExpiresAt.ToString("o"))

                        return Ok(jsonString payload)
                    | Error e -> return Error e
                }
        )

        let client = new Client(clientT, Bearer "secret")
        client.PermissionHandler <- Some(AlwaysAllowPermissionHandler())

        let! _ = client.OpenAsync(Capabilities.empty, CancellationToken.None)
        let! result = client.InvokeAsync("refund", jsonString "go")

        do! runtime.StopAsync()
        do! (runtime :> IAsyncDisposable).DisposeAsync()
        do! (client :> IAsyncDisposable).DisposeAsync()

        match result with
        | Ok(Some v) ->
            printfn "permission granted: %s" (v.GetString())
            return 0
        | other ->
            eprintfn "unexpected: %A" other
            return 1
    }
    |> fun t -> t.GetAwaiter().GetResult()
