-- ARCP event log schema (RFC §6.4, §13.3, §19).
-- Append-only log keyed by (session_id, message_id). Idempotent insert via UNIQUE.
-- Schema is applied at runtime startup; migrations are out of scope for v0.1.

CREATE TABLE IF NOT EXISTS events (
    seq            INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id     TEXT NOT NULL,
    message_id     TEXT NOT NULL,
    type           TEXT NOT NULL,
    job_id         TEXT NULL,
    stream_id      TEXT NULL,
    subscription_id TEXT NULL,
    trace_id       TEXT NULL,
    correlation_id TEXT NULL,
    causation_id   TEXT NULL,
    priority       TEXT NOT NULL DEFAULT 'normal',
    timestamp      TEXT NOT NULL,
    inserted_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    envelope_json  TEXT NOT NULL,
    UNIQUE(session_id, message_id)
);

CREATE INDEX IF NOT EXISTS idx_events_session_seq ON events(session_id, seq);
CREATE INDEX IF NOT EXISTS idx_events_session_type ON events(session_id, type);
CREATE INDEX IF NOT EXISTS idx_events_trace ON events(trace_id);
CREATE INDEX IF NOT EXISTS idx_events_job ON events(job_id);
CREATE INDEX IF NOT EXISTS idx_events_stream ON events(stream_id);

CREATE TABLE IF NOT EXISTS idempotency (
    session_principal TEXT NOT NULL,
    idempotency_key   TEXT NOT NULL,
    response_json     TEXT NOT NULL,
    created_at        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY(session_principal, idempotency_key)
);

CREATE TABLE IF NOT EXISTS artifacts (
    artifact_id   TEXT PRIMARY KEY,
    session_id    TEXT NOT NULL,
    media_type    TEXT NOT NULL,
    size_bytes    INTEGER NOT NULL,
    sha256        TEXT NOT NULL,
    expires_at    TEXT NULL,
    created_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    body          BLOB NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_artifacts_session ON artifacts(session_id);
CREATE INDEX IF NOT EXISTS idx_artifacts_expires ON artifacts(expires_at);
