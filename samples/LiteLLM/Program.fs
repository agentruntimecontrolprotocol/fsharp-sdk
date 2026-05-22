module ArcpSamples.LiteLLM

open System
open System.Linq
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ARCP.Core
open ARCP.Runtime
open ArcpSamples.SampleHarness

type LiteLLMProvisioner(baseUrl: Uri, adminKey: string, http: HttpClient) =
    let modelPatterns (lease: LeaseGrant) =
        Map.tryFind Capabilities.ModelUse lease.Capabilities |> Option.defaultValue []

    let maxBudget (lease: LeaseGrant) =
        Map.tryFind Capabilities.CostBudget lease.Capabilities
        |> Option.bind (
            List.tryPick (fun amount ->
                match Lease.parseBudgetAmount amount with
                | Ok(_, value) -> Some value
                | Error _ -> None)
        )

    let postJsonAsync (path: string) (body: obj) (ct: CancellationToken) =
        task {
            use request = new HttpRequestMessage(HttpMethod.Post, Uri(baseUrl, path))
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", adminKey)
            request.Content <- new StringContent(JsonSerializer.Serialize body, Encoding.UTF8, "application/json")
            let! response = http.SendAsync(request, ct)
            response.EnsureSuccessStatusCode() |> ignore
            return! response.Content.ReadAsStringAsync ct
        }

    interface ICredentialProvisioner with
        member _.IssueAsync(ctx, ct) =
            task {
                let body =
                    {|
                        key_alias = ctx.JobId.Value
                        duration =
                            ctx.LeaseConstraints
                            |> Option.map (fun c -> c.ExpiresAt - DateTimeOffset.UtcNow)
                            |> Option.map (fun span -> max 1 (int span.TotalSeconds))
                            |> Option.defaultValue 300
                        models = modelPatterns ctx.Lease |> List.toArray
                        max_budget = maxBudget ctx.Lease |> Option.toNullable
                    |}
                    :> obj

                let! raw = postJsonAsync "/key/generate" body ct
                use doc = JsonDocument.Parse raw
                let root = doc.RootElement

                let value =
                    match root.TryGetProperty("key") with
                    | true, p -> p.GetString()
                    | _ ->
                        match root.TryGetProperty("token") with
                        | true, p -> p.GetString()
                        | _ -> null

                if String.IsNullOrWhiteSpace value then
                    return raise (InvalidOperationException "LiteLLM response did not include key or token")
                else
                    let credential: Credential =
                        {
                            Id = CredentialId.newId ()
                            Scheme = "bearer"
                            Value = value
                            Endpoint = baseUrl.ToString().TrimEnd('/')
                            Profile = Some "litellm"
                            Constraints =
                                Some
                                    {
                                        CostBudget = Map.tryFind Capabilities.CostBudget ctx.Lease.Capabilities
                                        ModelUse = Map.tryFind Capabilities.ModelUse ctx.Lease.Capabilities
                                        ExpiresAt = ctx.LeaseConstraints |> Option.map (fun c -> c.ExpiresAt)
                                    }
                        }

                    return [ credential ]
            }

        member _.RevokeAsync(credentialId, ct) =
            task {
                let body = {| key = credentialId |} :> obj
                let! _ = postJsonAsync "/key/delete" body ct
                return true
            }

[<EntryPoint>]
let main _argv =
    runAsync (fun () ->
        task {
            let baseUrl = Environment.GetEnvironmentVariable "LITELLM_BASE_URL"
            let adminKey = Environment.GetEnvironmentVariable "LITELLM_ADMIN_KEY"

            if String.IsNullOrWhiteSpace baseUrl || String.IsNullOrWhiteSpace adminKey then
                writeLine "Set LITELLM_BASE_URL and LITELLM_ADMIN_KEY to run this sample."
                return 0
            else
                use http = new HttpClient()

                let provisioner =
                    LiteLLMProvisioner(Uri(baseUrl), adminKey, http) :> ICredentialProvisioner

                let withLiteLLM (options: ArcpServerOptions) =
                    { options with
                        Provisioner = Some provisioner
                        CredentialStore = Some(InMemoryCredentialStore() :> ICredentialStore)
                    }

                let features =
                    Set.ofList [ Features.ProvisionedCredentials; Features.ModelUse; Features.LeaseExpiresAt ]

                let! p =
                    connectWithOptions
                        withLiteLLM
                        (fun s ->
                            s.RegisterAgent(
                                "llm",
                                fun ctx ->
                                    task {
                                        do!
                                            ctx.ValidateOpAsync(
                                                Capabilities.ModelUse,
                                                "gpt-4o-mini",
                                                ctx.CancellationToken
                                            )

                                        return jsonString "LiteLLM credential issued"
                                    }
                            ))
                        features

                let lease =
                    Lease.empty
                    |> Lease.withCapability Capabilities.ModelUse [ "gpt-4o-mini" ]
                    |> Lease.withCapability Capabilities.CostBudget [ "USD:1.00" ]

                let! handle =
                    p.Client.SubmitAsync(
                        {
                            Agent = "llm"
                            Input = jsonInt 0
                            LeaseRequest = Some lease
                            LeaseConstraints =
                                Some
                                    {
                                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes 10.0
                                    }
                            IdempotencyKey = None
                            MaxRuntimeSec = None
                        },
                        CancellationToken.None
                    )

                writeLine (sprintf "issued %d LiteLLM credential(s)" handle.Credentials.Length)
                let! _ = handle.Result
                do! teardown p
                return 0
        })
