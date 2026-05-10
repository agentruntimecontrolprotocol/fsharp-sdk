namespace ARCP.Runtime

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ARCP.Errors
open ARCP.Ids
open ARCP.Messages.Execution

/// <summary>
/// In-memory artifact store (RFC §16). Phase 5 stores artifact bytes inline
/// keyed by <see cref="ArtifactId"/>; a periodic sweeper removes expired
/// entries.
/// </summary>
module Artifact =

    /// <summary>Format the canonical artifact URI (RFC §16.1).</summary>
    let formatUri (sessionId: SessionId) (artifactId: ArtifactId) : string =
        sprintf "arcp://session/%s/artifact/%s" (SessionId.value sessionId) (ArtifactId.value artifactId)

    /// <summary>SHA-256 of the supplied bytes, hex-encoded (lowercase).</summary>
    let sha256Hex (bytes: byte[]) : string =
        use h = SHA256.Create()
        let raw = h.ComputeHash(bytes)
        Convert.ToHexString(raw).ToLowerInvariant()

/// <summary>
/// A stored artifact and its retention metadata.
/// </summary>
type StoredArtifact =
    {
        /// <summary>Stable artifact id (RFC §16.1).</summary>
        ArtifactId: ArtifactId
        /// <summary>Session that owns the artifact (RFC §16.1).</summary>
        SessionId: SessionId
        /// <summary>MIME type carried inline.</summary>
        MediaType: string
        /// <summary>Raw bytes (decoded from base64 on put).</summary>
        Data: byte[]
        /// <summary>Size in bytes.</summary>
        Size: int64
        /// <summary>Content hash, lowercase hex (RFC §16.1).</summary>
        Sha256: string
        /// <summary>Creation timestamp (server-side).</summary>
        CreatedAt: DateTimeOffset
        /// <summary>Expiry timestamp (sweeper removes after this).</summary>
        ExpiresAt: DateTimeOffset
    }

/// <summary>
/// In-memory artifact store with periodic expiry sweep (RFC §16.2).
/// </summary>
type ArtifactStore
    (timeProvider: TimeProvider, defaultRetention: TimeSpan, ?maxRetention: TimeSpan, ?sweepInterval: TimeSpan) =

    let maxRetention = defaultArg maxRetention (TimeSpan.FromHours 24.0)
    let sweepInterval = defaultArg sweepInterval (TimeSpan.FromSeconds 60.0)
    let store = ConcurrentDictionary<ArtifactId, StoredArtifact>()

    let clampRetention (requested: TimeSpan option) : TimeSpan =
        let chosen = defaultArg requested defaultRetention

        if chosen > maxRetention then maxRetention
        elif chosen < TimeSpan.Zero then defaultRetention
        else chosen

    let sweep () : Task =
        task {
            let now = timeProvider.GetUtcNow()

            for kv in store do
                if kv.Value.ExpiresAt <= now then
                    store.TryRemove kv.Key |> ignore
        }
        :> Task

    let mutable timer: ITimer option = None

    do
        let cb = TimerCallback(fun _ -> sweep().GetAwaiter().GetResult())
        let t = timeProvider.CreateTimer(cb, null, sweepInterval, sweepInterval)
        timer <- Some t

    /// <summary>
    /// Validate &amp; persist an inline artifact (RFC §16.1). Returns its
    /// <see cref="ArtifactRef"/>.
    /// </summary>
    member _.PutAsync
        (sessionId: SessionId, mediaType: string, base64Data: string, ?sha256: string, ?retention: TimeSpan)
        : Task<Result<ArtifactRef, ARCPError>> =
        task {
            try
                let bytes = Convert.FromBase64String base64Data
                let actualHash = Artifact.sha256Hex bytes

                match sha256 with
                | Some claimed when not (String.Equals(claimed, actualHash, StringComparison.OrdinalIgnoreCase)) ->
                    return Error(InvalidArgument("sha256", "claimed digest does not match data"))
                | _ ->
                    let now = timeProvider.GetUtcNow()
                    let ttl = clampRetention retention
                    let expiresAt = now + ttl
                    let aid = ArtifactId.create ()

                    let stored: StoredArtifact =
                        {
                            ArtifactId = aid
                            SessionId = sessionId
                            MediaType = mediaType
                            Data = bytes
                            Size = int64 bytes.LongLength
                            Sha256 = actualHash
                            CreatedAt = now
                            ExpiresAt = expiresAt
                        }

                    store.[aid] <- stored

                    let r: ArtifactRef =
                        {
                            ArtifactId = aid
                            Uri = Artifact.formatUri sessionId aid
                            MediaType = mediaType
                            Size = stored.Size
                            Sha256 = actualHash
                            ExpiresAt = Some expiresAt
                        }

                    return Ok r
            with
            | :? FormatException as fx -> return Error(InvalidArgument("data", fx.Message))
            | ex -> return Error(Internal(ex.Message, Some ex))
        }

    /// <summary>Retrieve a stored artifact by id, honouring expiry.</summary>
    member _.FetchAsync(artifactId: ArtifactId) : Task<Result<StoredArtifact, ARCPError>> =
        task {
            match store.TryGetValue artifactId with
            | true, a when a.ExpiresAt > timeProvider.GetUtcNow() -> return Ok a
            | true, _ ->
                store.TryRemove artifactId |> ignore
                return Error(NotFound(sprintf "artifact %s" (ArtifactId.value artifactId)))
            | _ -> return Error(NotFound(sprintf "artifact %s" (ArtifactId.value artifactId)))
        }

    /// <summary>Reconstruct an <see cref="ArtifactRef"/> for a stored id.</summary>
    member this.RefAsync(artifactId: ArtifactId) : Task<Result<ArtifactRef, ARCPError>> =
        task {
            let! got = this.FetchAsync artifactId

            return
                got
                |> Result.map (fun a ->
                    {
                        ArtifactId = a.ArtifactId
                        Uri = Artifact.formatUri a.SessionId a.ArtifactId
                        MediaType = a.MediaType
                        Size = a.Size
                        Sha256 = a.Sha256
                        ExpiresAt = Some a.ExpiresAt
                    })
        }

    /// <summary>Drop an artifact early (RFC §16.1).</summary>
    member _.ReleaseAsync(artifactId: ArtifactId) : Task<Result<unit, ARCPError>> =
        task {
            match store.TryRemove artifactId with
            | true, _ -> return Ok()
            | _ -> return Error(NotFound(sprintf "artifact %s" (ArtifactId.value artifactId)))
        }

    /// <summary>Run the sweeper once synchronously (test affordance).</summary>
    member _.SweepNowAsync() : Task = sweep ()

    /// <summary>Number of live artifacts.</summary>
    member _.Count: int = store.Count

    interface IDisposable with
        member _.Dispose() =
            match timer with
            | Some t -> t.Dispose()
            | None -> ()
