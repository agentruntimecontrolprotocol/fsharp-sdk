#!/usr/bin/env dotnet-script
// stream-resume — Server
//
// A long-form content writer that streams its output using ARCP result
// chunks.  The server buffers ~200 characters then flushes each as a
// job.stream_chunk event.  A client that disconnects can reconnect and
// resume from where it left off using the event log (60-second window).
//
// Run:
//   dotnet script Server.fsx

#r "nuget: Arcp, 1.0.0"
#r "nuget: Microsoft.AspNetCore.App.Ref, 10.0.0"

open System
open System.Text
open System.Text.Json
open System.Threading
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open ARCP.Public

// ---------------------------------------------------------------------------
// Simulated long-form content generator
// ---------------------------------------------------------------------------

/// Yield simulated chunks of a long article, 200 chars at a time.
let generateArticle (topic: string) (ct: CancellationToken) =
    let fullText =
        sprintf """# %s

This is a comprehensive exploration of %s.  The topic spans several
fascinating dimensions that we will examine in depth.

## Background

The history of %s dates back many decades.  Early pioneers recognised
the potential of this field and laid the groundwork for what would
become a transformative discipline.  Their contributions, while modest
in scope at the time, have had far-reaching consequences for modern
practice.

## Core Concepts

Understanding %s requires familiarity with several key ideas.
First, we must appreciate the fundamental tension between simplicity
and expressiveness.  Second, scalability constraints impose hard limits
that no engineering approach can fully escape.  Third, the human
element — intuition, creativity, and collaboration — remains
irreducible even in the most automated systems.

## Recent Advances

The last five years have seen remarkable progress in %s.  New
techniques have dramatically lowered the barrier to entry while
simultaneously raising the ceiling of what is achievable.  Open-source
tooling has democratised access and accelerated the pace of
experimentation.

## Conclusion

The future of %s is bright.  With sustained investment, continued
collaboration, and thoughtful stewardship of the field's values, we
can expect breakthroughs that benefit society broadly.
""" topic topic topic topic topic topic

    seq {
        let mutable i = 0
        while i < fullText.Length && not ct.IsCancellationRequested do
            let len = min 200 (fullText.Length - i)
            yield fullText.Substring(i, len)
            i <- i + len
    }

// ---------------------------------------------------------------------------
// Agent handler
// ---------------------------------------------------------------------------

let writerHandler (ctx: JobContext) =
    task {
        let ct = ctx.CancellationToken
        let topic =
            if ctx.Input.TryGetProperty("topic") |> fst
            then ctx.Input.GetProperty("topic").GetString()
            else "artificial intelligence"

        do! ctx.EmitStatusAsync("writing", Some (sprintf "Generating article on: %s" topic), ct)

        // Open a streaming result — returns a handle we write chunks to
        let stream = ctx.BeginStreamingResult()

        let chunks = generateArticle topic ct |> Seq.toArray
        let total = chunks.Length

        for i, chunk in Array.indexed chunks do
            do! System.Threading.Tasks.Task.Delay(80, ct)  // simulate generation latency
            do! ctx.EmitResultChunkAsync(stream, chunk, i, ct)

        // Finalize with the full text reconstructed and a brief summary
        let fullText = String.concat "" chunks
        let summary = sprintf "Article on '%s' — %d characters, %d sections." topic fullText.Length total
        do! ctx.FinalizeStreamingResultAsync(stream, fullText, {| summary = summary |}, ct)

        return JsonSerializer.SerializeToElement({| topic = topic; char_count = fullText.Length; chunk_count = total |})
    }

// ---------------------------------------------------------------------------
// Server options — enable event log for resume support
// ---------------------------------------------------------------------------

let serverOptions =
    { ArcpServerOptions.defaults with
        Features = { Features.All with EventLog = true }
        ResumeWindowSeconds = 60 }

// ---------------------------------------------------------------------------
// Wire up and serve
// ---------------------------------------------------------------------------

let server = new ArcpServer(serverOptions)
server.RegisterAgent("writer", writerHandler)

let builder = WebApplication.CreateBuilder()
builder.Services.AddArcp(fun opts ->
    opts.Features             <- serverOptions.Features
    opts.ResumeWindowSeconds  <- serverOptions.ResumeWindowSeconds) |> ignore

let app = builder.Build()
app.UseWebSockets()
app.MapArcp("/arcp")

printfn "Stream-resume server listening on ws://localhost:5002/arcp"
app.Run("http://localhost:5002")
