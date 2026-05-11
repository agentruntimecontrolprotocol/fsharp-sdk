/// SDR domain via custom `arcpx.sdr.*.v1` extension messages.
///
/// Tune to 145.500 MHz (2 m FM calling), capture 5 s of IQ at 2.048 MS/s,
/// NBFM-demodulate to 48 kHz PCM. Exercises §21 naming, capability
/// advertisement, and unknown-message handling.
module ARCP.Samples.Extensions.Program

open System.Text.Json
open System.Threading.Tasks
open ARCP.Client
open ARCP.Errors

let extTune = "arcpx.sdr.tune.v1"
let extGain = "arcpx.sdr.gain.v1"
let extCapture = "arcpx.sdr.capture.v1"
let extDemodulate = "arcpx.sdr.demodulate.v1"
let allExtensions = [ extTune; extGain; extCapture; extDemodulate ]

/// Fire one ext request via tool.invoke shape and return the raw payload.
let request (client: Client) (extType: string) (payload: JsonElement) : Task<JsonElement> =
    task {
        // In the SDK these would be addressed through Client.SendExtensionAsync(extType, payload, ...)
        // and correlated like any other request/response pair.
        return failwith "elided: ext envelope round-trip"
    }

[<EntryPoint>]
let main _ =
    task {
        let client: Client = Unchecked.defaultof<_> // capabilities.extensions = allExtensions on session.open
        // let! sessionId = client.OpenAsync(...)

        // If the runtime didn't advertise our required extension set, refuse the
        // session — RFC §7 / §21.2.
        // let advertised = accepted.Capabilities.Extensions |> Option.defaultValue []
        // if not (Set.isSubset (Set.ofList allExtensions) (Set.ofList advertised)) then
        //     raise (exn (ARCPError.message (Unimplemented "runtime missing SDR extensions")))

        let! _ =
            request
                client
                extTune
                (JsonDocument
                    .Parse(
                        """
                    {"center_freq_hz":145500000.0,
                     "sample_rate_hz":2048000.0,
                     "ppm_correction":1}"""
                    )
                    .RootElement)

        let! _ =
            request
                client
                extGain
                (JsonDocument
                    .Parse(
                        """
                    {"stages":[{"name":"TUNER","value_db":28.0}]}"""
                    )
                    .RootElement)

        // Capture returns an artifact.ref pointing at the IQ buffer. The buffer
        // never travels inline — demodulate references it.
        let! cap =
            request
                client
                extCapture
                (JsonDocument
                    .Parse(
                        """
                    {"seconds":5.0,"capture_handle":"abc12345","decimate":1}"""
                    )
                    .RootElement)

        let iqArtifact = cap.GetProperty("artifact_id").GetString()
        printfn "captured IQ → %s" iqArtifact

        let! audio =
            request
                client
                extDemodulate
                (JsonDocument
                    .Parse(sprintf """{"iq_artifact_id":"%s","mode":"NBFM","audio_rate_hz":48000}""" iqArtifact)
                    .RootElement)

        printfn "demod  PCM → %s" (audio.GetProperty("artifact_id").GetString())

        // §21.3 demonstration: unadvertised extension marked optional. Runtime
        // SHOULD ack (silent drop) rather than nack.
        let! optional =
            request
                client
                "arcpx.sdr.experimental_doppler.v1"
                (JsonDocument.Parse("""{"velocity_mps":7.4}""").RootElement)
        // (extensions = { optional = true })

        printfn "optional unknown → %A" optional
        return ()
    }
    |> fun t -> t.GetAwaiter().GetResult()

    0
