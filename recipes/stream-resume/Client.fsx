#!/usr/bin/env dotnet-script
// stream-resume — Client
//
// Demonstrates mid-stream disconnect and seamless resume.
//
// Session 1: Connect, submit a writer job, collect the first few chunks,
//            then intentionally drop the transport.
//
// Session 2: Reconnect and call client.ResumeAsync with the session_id,
//            resume_token, and last seen event sequence number.  The
//            server replays missed events from its event log before
//            continuing the live stream.
//
// The client reassembles all chunks in order and prints the final article.
//
// Run:
//   dotnet script Client.fsx

#r "nuget: Arcp, 1.0.0"

open System
open System.Collections.Generic
open System.Net.WebSockets
open System.Text.Json
open System.Threading
open ARCP.Public

let mkTransport (url: string) =
    task {
        let ws = new ClientWebSocket()
        do! ws.ConnectAsync(Uri(url), CancellationToken.None)
        return WebSocketTransport.fromClientSocket ws
    }

let run () =
    task {
        let url = "ws://localhost:5002/arcp"

        // -----------------------------------------------------------------------
        // Session 1 — submit and collect some chunks, then disconnect
        // -----------------------------------------------------------------------
        printfn "=== Session 1: submit and collect initial chunks ==="

        let! t1 = mkTransport url
        use client1 = new ArcpClient(ArcpClientOptions.defaults)

        let handle1 =
            client1.Submit(
                t1,
                { JobSubmitRequest.defaults with
                    Agent = "writer"
                    Input = JsonSerializer.SerializeToElement({| topic = "quantum error correction" |}) },
                CancellationToken.None)

        let jobId       = handle1.JobId
        let sessionInfo = handle1.SessionInfo   // carries session_id + resume_token

        printfn "Job %s  session %s" (jobId.ToString()) (sessionInfo.SessionId.ToString())

        let chunks   = SortedDictionary<int, string>()  // chunk_seq → text
        let mutable lastSeq = 0

        // Collect for 800 ms then drop
        use cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800.0))

        try
            for event in handle1.Events do
                lastSeq <- event.Seq
                match event.Body with
                | JobEventBody.ResultChunk(text, chunkSeq) ->
                    chunks[chunkSeq] <- text
                    printf "."
                | JobEventBody.Status(state, msg) ->
                    printfn "\n[status] %s  %s" state (defaultArg msg "")
                | _ -> ()
        with :? OperationCanceledException -> ()

        printfn "\nDropping transport after %d chunks (last_event_seq=%d)" chunks.Count lastSeq

        // -----------------------------------------------------------------------
        // Session 2 — reconnect and resume
        // -----------------------------------------------------------------------
        printfn ""
        printfn "=== Session 2: reconnect and resume ==="

        do! System.Threading.Tasks.Task.Delay(200)  // brief pause before reconnecting

        let! t2 = mkTransport url
        use client2 = new ArcpClient(ArcpClientOptions.defaults)

        let handle2 =
            client2.Resume(
                t2,
                { ResumeRequest.defaults with
                    JobId           = jobId
                    SessionId       = sessionInfo.SessionId
                    ResumeToken     = sessionInfo.ResumeToken
                    LastSeenEventSeq = lastSeq },
                CancellationToken.None)

        for event in handle2.Events do
            lastSeq <- event.Seq
            match event.Body with
            | JobEventBody.ResultChunk(text, chunkSeq) ->
                chunks[chunkSeq] <- text
                printf "+"
            | JobEventBody.Status(state, msg) ->
                printfn "\n[status] %s  %s" state (defaultArg msg "")
            | _ -> ()

        let! result = handle2.Result
        printfn ""

        match result with
        | Ok output ->
            // Reassemble in chunk_seq order
            let article = chunks.Values |> String.concat ""

            printfn "=== Assembled article (%d chars, %d chunks) ===" article.Length chunks.Count
            printfn ""
            printfn "%s" (if article.Length > 500 then article.[..499] + "..." else article)
            printfn ""
            printfn "Job metadata: %s" (output.GetRawText())
        | Error err ->
            printfn "Job failed: %s" (err.ToString())
    }

run () |> Async.AwaitTask |> Async.RunSynchronously
