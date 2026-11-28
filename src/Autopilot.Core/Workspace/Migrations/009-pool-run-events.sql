-- What happened to a pool run, in the order it happened. The event name is a PoolRunEvent name.
CREATE TABLE IF NOT EXISTS pool_run_events (
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id  TEXT NOT NULL,
    at      TEXT NOT NULL,
    event   TEXT NOT NULL,
    task_id TEXT,
    detail  TEXT
);
CREATE INDEX IF NOT EXISTS ix_pool_run_events_run ON pool_run_events (run_id);
