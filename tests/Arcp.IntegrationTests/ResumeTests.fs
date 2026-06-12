module ARCP.IntegrationTests.ResumeTests

open System.Threading
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open ARCP.Core
open ARCP.Client
open ARCP.Client.Transport
open ARCP.Runtime
open ARCP.Runtime.Auth

/// Server-level resume tests (spec §6.3). These drive raw envelopes so
/// the `session.resume` flow can be exercised end-to-end.

let private hello: SessionHelloPayload =
    {
        Client = { Name = "t"; Version = "1" }
        Auth =
            {
                Scheme = "bearer"
                Token = Some "tok"
            }
        Capabilities =
            {
                Encodings = [ "json" ]
                Features = Features.All
            }
    }

let private send (t: ITransport) (msg: Message) (sid: string option) : Task =
    let env = Codec.toEnvelope msg

    let env =
        match sid with
        | Some s -> { env with SessionId = Some s }
        | None -> env

    t.SendAsync(env, CancellationToken.None)

/// Drain envelopes until `stopWhen` matches one or the timeout elapses.
let private drain (t: ITransport) (stopWhen: Envelope -> bool) (timeoutMs: int) : Task<Envelope list> =
    task {
        use cts = new CancellationTokenSource(timeoutMs)
        let acc = ResizeArray<Envelope>()
        let en = (t.Receive(cts.Token)).GetAsyncEnumerator(cts.Token)
        let mutable more = true

        try
            while more do
                let! has = en.MoveNextAsync().AsTask()

                if has then
                    acc.Add en.Current
                    if stopWhen en.Current then more <- false
                else
                    more <- false
        with _ ->
            ()

        do! en.DisposeAsync().AsTask()
        return List.ofSeq acc
    }

let private emitterServer () : ArcpServer =
    let server =
        new ArcpServer(
            { ArcpServerOptions.defaults with
                Features = Features.All
                BearerVerifier = DevModeBearerVerifier()
            }
        )

    server.RegisterAgent(
        "emitter",
        fun ctx ->
            task {
                do! ctx.EmitLogAsync(LogLevel.Info, "e1", ctx.CancellationToken)
                do! ctx.EmitLogAsync(LogLevel.Info, "e2", ctx.CancellationToken)
                do! ctx.EmitLogAsync(LogLevel.Info, "e3", ctx.CancellationToken)
                return Json.serializeToElement<string> "done"
            }
    )

    server

[<Fact>]
let ``session.resume replays buffered events`` () =
    task {
        use cts = new CancellationTokenSource()
        let server = emitterServer ()

        // First connection: handshake, submit, drain through job.result.
        let c1, s1 = MemoryTransport.CreatePair()
        let st1 = server.HandleSessionAsync(s1, cts.Token)
        do! send c1 (Message.SessionHello hello) None
        let! welcomeBatch = drain c1 (fun e -> e.Type = "session.welcome") 1000
        let welcomeEnv = welcomeBatch |> List.find (fun e -> e.Type = "session.welcome")
        let sid = welcomeEnv.SessionId.Value

        let token =
            match Codec.toMessage welcomeEnv with
            | Ok(Message.SessionWelcome w) -> w.ResumeToken
            | _ -> failwith "expected welcome"

        do!
            send
                c1
                (Message.JobSubmit
                    {
                        Agent = "emitter"
                        Input = Json.serializeToElement<int> 0
                        LeaseRequest = None
                        LeaseConstraints = None
                        IdempotencyKey = None
                        MaxRuntimeSec = None
                    })
                (Some sid)

        let! _firstEvents = drain c1 (fun e -> e.Type = "job.result") 1500

        // Simulate a transport drop and let the server move the session
        // into the resumable set.
        do! c1.CloseAsync CancellationToken.None
        do! st1

        // Reconnect on a fresh transport and resume from seq 0.
        let c2, s2 = MemoryTransport.CreatePair()
        let st2 = server.HandleSessionAsync(s2, cts.Token)

        do!
            send
                c2
                (Message.SessionResume
                    {
                        SessionId = sid
                        ResumeToken = token
                        LastEventSeq = 0L
                    })
                None

        let! resumed = drain c2 (fun e -> e.Type = "job.result") 1500

        resumed
        |> List.filter (fun e -> e.Type = "session.welcome")
        |> List.isEmpty
        |> should equal false

        let events = resumed |> List.filter (fun e -> e.Type = "job.event")
        events |> List.length |> should be (greaterThanOrEqualTo 3)

        cts.Cancel()
        do! st2
    }

[<Fact>]
let ``session.resume with unknown token returns RESUME_WINDOW_EXPIRED`` () =
    task {
        use cts = new CancellationTokenSource()
        let server = emitterServer ()

        let c1, s1 = MemoryTransport.CreatePair()
        let st1 = server.HandleSessionAsync(s1, cts.Token)
        do! send c1 (Message.SessionHello hello) None
        let! welcomeBatch = drain c1 (fun e -> e.Type = "session.welcome") 1000
        let sid = (welcomeBatch |> List.find (fun e -> e.Type = "session.welcome")).SessionId.Value
        do! c1.CloseAsync CancellationToken.None
        do! st1

        let c2, s2 = MemoryTransport.CreatePair()
        let st2 = server.HandleSessionAsync(s2, cts.Token)

        do!
            send
                c2
                (Message.SessionResume
                    {
                        SessionId = sid
                        ResumeToken = "wrong-token"
                        LastEventSeq = 0L
                    })
                None

        let! batch = drain c2 (fun e -> e.Type = "session.error") 1000
        let errEnv = batch |> List.tryFind (fun e -> e.Type = "session.error")

        match errEnv with
        | Some e ->
            match Codec.toMessage e with
            | Ok(Message.SessionError p) -> p.Code |> should equal "RESUME_WINDOW_EXPIRED"
            | _ -> failwith "expected session.error"
        | None -> failwith "expected session.error envelope"

        cts.Cancel()
        do! st2
    }
