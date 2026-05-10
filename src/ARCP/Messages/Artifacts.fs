namespace ARCP.Messages

open ARCP.Ids

/// <summary>Artifact payload records (RFC §16).</summary>
module Artifacts =

    /// <summary>
    /// Reference to a content-addressed artifact. Re-exported from
    /// <see cref="ARCP.Messages.Execution.ArtifactRef"/> for ergonomic
    /// imports.
    /// </summary>
    type ArtifactRef = ARCP.Messages.Execution.ArtifactRef

    /// <summary><c>artifact.put</c> payload (RFC §16.1).</summary>
    type ArtifactPut =
        {
            MediaType: string
            Data: string
            Sha256: string option
        }

    /// <summary><c>artifact.fetch</c> payload (RFC §16.1).</summary>
    type ArtifactFetch = { ArtifactId: ArtifactId }

    /// <summary><c>artifact.release</c> payload (RFC §16.1).</summary>
    type ArtifactRelease = { ArtifactId: ArtifactId }
