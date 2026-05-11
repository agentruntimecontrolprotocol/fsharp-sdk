/// Sandboxed on-call agent. Lease-gated shell, reasoning streamed.
module ARCP.Samples.Leases.Program

open System
open System.Text.Json
open FSharp.Control
open ARCP.Client
open ARCP.Errors
open ARCP.Ids
open ARCP.Samples.Leases.Agent

let readBinaries =
    Set.ofList [ "/usr/bin/journalctl"; "/usr/bin/cat"; "/usr/bin/ss"; "/usr/bin/ps" ]

let writeBinaries = Set.ofList [ "/usr/bin/systemctl"; "/usr/bin/kill" ]
let readLeaseSeconds = 30 * 60
let writeLeaseSeconds = 60

type Classification =
    {
        Permission: string
        Resource: string
        Operation: string
        LeaseSeconds: int
    }

let classify (argv: string list) (host: string) : Classification =
    let binary = List.head argv

    if Set.contains binary readBinaries then
        {
            Permission = "host.read"
            Resource = sprintf "host:%s" host
            Operation = "read"
            LeaseSeconds = readLeaseSeconds
        }
    elif Set.contains binary writeBinaries then
        let target =
            if binary = "/usr/bin/systemctl" then
                List.item 2 argv
            else
                List.item 1 argv

        {
            Permission = "host.write"
            Resource = sprintf "host:%s/%s/%s" host binary target
            Operation = "write"
            LeaseSeconds = writeLeaseSeconds
        }
    else
        let err =
            ARCPError.PermissionDenied(binary, sprintf "binary not allowed: %s" binary)

        raise (exn (ARCPError.message err))

/// Issue permission.request; on permission.deny throws PERMISSION_DENIED.
let acquireLease (client: Client) (c: Classification) (reason: string) : System.Threading.Tasks.Task<string> =
    task {
        // Real call: client.RequestPermissionAsync(...) — translates to permission.request
        // and awaits permission.grant / permission.deny.
        return failwith "elided: permission.request → lease_id"
    }

let runCommand (client: Client) (argv: string list) (reason: string) (host: string) =
    task {
        let c = classify argv host
        let! lease = acquireLease client c reason
        // Lease is the only guard; subprocess is spawned elsewhere.
        return sprintf "<would run %A under lease %s>" argv lease
    }

let emitThought (client: Client) (streamId: StreamId) (sequence: int) (text: string) =
    task {
        // Real call: client.SendStreamChunkAsync(streamId, kind="thought", role="assistant_thought", content=text)
        return failwith "elided: stream.chunk kind=thought"
    }

[<EntryPoint>]
let main _ =
    task {
        let client: Client = Unchecked.defaultof<_> // transport, identity (constrained), auth elided
        // do! client.OpenAsync(...) |> Task.map ignore

        let streamId = StreamId.create ()
        // do! client.OpenStreamAsync(streamId, kind = "thought")

        let mutable seq = 0

        do!
            llmLoop "api-gateway pod is OOMing every 4 minutes"
            |> TaskSeq.iterAsync (fun step ->
                task {
                    do! emitThought client streamId seq step.Thought
                    seq <- seq + 1

                    match step.ToolCall with
                    | Some tc ->
                        try
                            let! _ = runCommand client tc.Argv tc.Reason "edge-pod-04"
                            ()
                        with _ ->
                            () // PERMISSION_DENIED feeds back into the next prompt
                    | None -> ()

                    match step.Final with
                    | Some final -> printfn "%s" final
                    | None -> ()
                })
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
