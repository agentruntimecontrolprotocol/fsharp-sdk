module ARCP.Store.EventLog

open System
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Microsoft.Data.Sqlite
open ARCP
open ARCP.Ids
open ARCP.Envelope

/// <summary>Configuration for an <see cref="EventLog"/>.</summary>
type EventLogOptions =
    { /// <summary>SQLite connection string.</summary>
      ConnectionString: string }

[<RequireQualifiedAccess>]
module EventLogOptions =
    /// <summary>Use an isolated in-memory database (one per call).</summary>
    let memory () =
        let unique = Guid.NewGuid().ToString("N")
        { ConnectionString = sprintf "Data Source=arcp-%s;Mode=Memory;Cache=Shared" unique }

    /// <summary>Use a file-backed database.</summary>
    let file (path: string) =
        { ConnectionString = sprintf "Data Source=%s;Cache=Shared" path }

let private schemaSql: string =
    let asm = Assembly.GetExecutingAssembly()
    let resourceName = "Schema.sql"

    let resolved =
        asm.GetManifestResourceNames()
        |> Array.tryFind (fun n -> n.EndsWith resourceName)
        |> Option.defaultWith (fun () -> failwithf "embedded resource %s not found" resourceName)

    use stream = asm.GetManifestResourceStream resolved
    use reader = new StreamReader(stream)
    reader.ReadToEnd()

/// <summary>A single envelope persisted in the event log.</summary>
type LoggedEvent =
    { Seq: int64
      SessionId: SessionId
      MessageId: MessageId
      Type: string
      JobId: JobId option
      StreamId: StreamId option
      SubscriptionId: SubscriptionId option
      TraceId: TraceId option
      CorrelationId: MessageId option
      CausationId: MessageId option
      Priority: string
      Timestamp: DateTimeOffset
      EnvelopeJson: string }

let private optString (reader: SqliteDataReader) (i: int) : string option =
    if reader.IsDBNull i then None else Some(reader.GetString i)

let private readEvent (reader: SqliteDataReader) : LoggedEvent =
    { Seq = reader.GetInt64 0
      SessionId = SessionId(reader.GetString 1)
      MessageId = MessageId(reader.GetString 2)
      Type = reader.GetString 3
      JobId = optString reader 4 |> Option.map JobId
      StreamId = optString reader 5 |> Option.map StreamId
      SubscriptionId = optString reader 6 |> Option.map SubscriptionId
      TraceId = optString reader 7 |> Option.map TraceId
      CorrelationId = optString reader 8 |> Option.map MessageId
      CausationId = optString reader 9 |> Option.map MessageId
      Priority = reader.GetString 10
      Timestamp = DateTimeOffset.Parse(reader.GetString 11)
      EnvelopeJson = reader.GetString 12 }

let private columnList =
    "seq, session_id, message_id, type, job_id, stream_id, subscription_id, "
    + "trace_id, correlation_id, causation_id, priority, timestamp, envelope_json"

/// <summary>
/// Append-only SQLite event log keyed by <c>(session_id, message_id)</c>.
/// Idempotent insert: re-appending an envelope with the same session+id is a
/// no-op (RFC §6.4 transport-level idempotency).
/// </summary>
type EventLog(options: EventLogOptions) =
    let connection = new SqliteConnection(options.ConnectionString)
    do connection.Open()

    do
        use cmd = connection.CreateCommand()
        cmd.CommandText <- schemaSql
        cmd.ExecuteNonQuery() |> ignore

    let lockObj = obj ()

    /// <summary>
    /// Append one envelope. Returns <c>true</c> if it was newly inserted,
    /// <c>false</c> if the session/message id pair was already present
    /// (idempotent retry).
    /// </summary>
    member _.AppendAsync<'P>(env: Envelope<'P>, ?cancellationToken: CancellationToken) : Task<bool> =
        let ct = defaultArg cancellationToken CancellationToken.None

        task {
            ct.ThrowIfCancellationRequested()
            let sessionId = env.SessionId |> Option.defaultValue (SessionId "<no-session>")
            let envelopeJson = Json.serialize env

            let inserted =
                lock lockObj (fun () ->
                    use cmd = connection.CreateCommand()

                    cmd.CommandText <-
                        "INSERT OR IGNORE INTO events"
                        + "(session_id, message_id, type, job_id, stream_id, subscription_id, trace_id, "
                        + "correlation_id, causation_id, priority, timestamp, envelope_json) "
                        + "VALUES($s,$m,$t,$j,$st,$su,$tr,$c,$ca,$p,$ts,$e)"

                    let bind name value =
                        cmd.Parameters.AddWithValue(name, box value) |> ignore

                    bind "$s" (SessionId.value sessionId)
                    bind "$m" (MessageId.value env.Id)
                    bind "$t" env.Type

                    bind
                        "$j"
                        (env.JobId
                         |> Option.map JobId.value
                         |> Option.map box
                         |> Option.defaultValue (box DBNull.Value))

                    bind
                        "$st"
                        (env.StreamId
                         |> Option.map StreamId.value
                         |> Option.map box
                         |> Option.defaultValue (box DBNull.Value))

                    bind
                        "$su"
                        (env.SubscriptionId
                         |> Option.map SubscriptionId.value
                         |> Option.map box
                         |> Option.defaultValue (box DBNull.Value))

                    bind
                        "$tr"
                        (env.TraceId
                         |> Option.map TraceId.value
                         |> Option.map box
                         |> Option.defaultValue (box DBNull.Value))

                    bind
                        "$c"
                        (env.CorrelationId
                         |> Option.map MessageId.value
                         |> Option.map box
                         |> Option.defaultValue (box DBNull.Value))

                    bind
                        "$ca"
                        (env.CausationId
                         |> Option.map MessageId.value
                         |> Option.map box
                         |> Option.defaultValue (box DBNull.Value))

                    bind
                        "$p"
                        (env.Priority
                         |> Option.map Priority.value
                         |> Option.defaultValue "normal")

                    bind "$ts" (env.Timestamp.ToString("O"))
                    bind "$e" envelopeJson
                    let rows = cmd.ExecuteNonQuery()
                    rows > 0)

            return inserted
        }

    /// <summary>Number of events recorded for a session.</summary>
    member _.CountAsync(sessionId: SessionId, ?cancellationToken: CancellationToken) : Task<int64> =
        let ct = defaultArg cancellationToken CancellationToken.None

        task {
            ct.ThrowIfCancellationRequested()

            return
                lock lockObj (fun () ->
                    use cmd = connection.CreateCommand()
                    cmd.CommandText <- "SELECT COUNT(1) FROM events WHERE session_id = $s"
                    cmd.Parameters.AddWithValue("$s", SessionId.value sessionId) |> ignore
                    cmd.ExecuteScalar() :?> int64)
        }

    /// <summary>
    /// Replay events for a session in insert order, optionally starting after
    /// the given message id (RFC §19 message-id resume).
    /// </summary>
    member _.Replay(sessionId: SessionId, ?afterMessageId: MessageId) : seq<LoggedEvent> =
        seq {
            let snapshot =
                lock lockObj (fun () ->
                    use cmd = connection.CreateCommand()

                    match afterMessageId with
                    | Some(MessageId mid) ->
                        cmd.CommandText <-
                            sprintf
                                "SELECT %s FROM events WHERE session_id = $s AND seq > (SELECT seq FROM events WHERE session_id = $s AND message_id = $m) ORDER BY seq"
                                columnList

                        cmd.Parameters.AddWithValue("$s", SessionId.value sessionId) |> ignore
                        cmd.Parameters.AddWithValue("$m", mid) |> ignore
                    | None ->
                        cmd.CommandText <-
                            sprintf "SELECT %s FROM events WHERE session_id = $s ORDER BY seq" columnList

                        cmd.Parameters.AddWithValue("$s", SessionId.value sessionId) |> ignore

                    use reader = cmd.ExecuteReader()
                    let buffer = ResizeArray<LoggedEvent>()

                    while reader.Read() do
                        buffer.Add(readEvent reader)

                    buffer)

            for ev in snapshot do
                yield ev
        }

    /// <summary>Look up a previous response for an idempotent logical command (RFC §6.4).</summary>
    member _.TryGetIdempotentResponse(sessionPrincipal: string, key: IdempotencyKey) : string option =
        lock lockObj (fun () ->
            use cmd = connection.CreateCommand()

            cmd.CommandText <-
                "SELECT response_json FROM idempotency WHERE session_principal = $p AND idempotency_key = $k"

            cmd.Parameters.AddWithValue("$p", sessionPrincipal) |> ignore
            cmd.Parameters.AddWithValue("$k", IdempotencyKey.value key) |> ignore

            match cmd.ExecuteScalar() with
            | :? string as s -> Some s
            | _ -> None)

    /// <summary>Record the response for an idempotent logical command.</summary>
    member _.RecordIdempotentResponse(sessionPrincipal: string, key: IdempotencyKey, responseJson: string) : unit =
        lock lockObj (fun () ->
            use cmd = connection.CreateCommand()

            cmd.CommandText <-
                "INSERT OR REPLACE INTO idempotency (session_principal, idempotency_key, response_json) VALUES($p,$k,$r)"

            cmd.Parameters.AddWithValue("$p", sessionPrincipal) |> ignore
            cmd.Parameters.AddWithValue("$k", IdempotencyKey.value key) |> ignore
            cmd.Parameters.AddWithValue("$r", responseJson) |> ignore
            cmd.ExecuteNonQuery() |> ignore)

    interface IDisposable with
        member _.Dispose() =
            connection.Close()
            connection.Dispose()
