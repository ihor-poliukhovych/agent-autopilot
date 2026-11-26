CREATE TABLE pool_items (
    project_key  TEXT    NOT NULL,
    work_item_id TEXT    NOT NULL,
    position     INTEGER NOT NULL,
    added_at     TEXT    NOT NULL,
    PRIMARY KEY (project_key, work_item_id)
);

CREATE TABLE jobs (
    id           TEXT NOT NULL PRIMARY KEY,
    project_key  TEXT NOT NULL,
    work_item_id TEXT NOT NULL,
    status       TEXT NOT NULL CHECK (status IN ('queued', 'running', 'succeeded', 'failed', 'cancelled')),
    step         TEXT,
    pid          INTEGER,
    created_at   TEXT NOT NULL,
    started_at   TEXT,
    finished_at  TEXT,
    log_path     TEXT NOT NULL,
    message      TEXT
);
CREATE INDEX jobs_by_project ON jobs (project_key, created_at);

CREATE TABLE runs (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    project_key       TEXT    NOT NULL,
    work_item_id      TEXT    NOT NULL,
    job_id            TEXT,
    started_at        TEXT    NOT NULL,
    finished_at       TEXT    NOT NULL,
    outcome           TEXT    NOT NULL,
    gate_verdict      TEXT    CHECK (gate_verdict IN ('passed', 'rejected')),
    high_risk         INTEGER NOT NULL DEFAULT 0,
    would_merge       INTEGER NOT NULL DEFAULT 0,
    human_decision    TEXT    CHECK (human_decision IN ('merged', 'rejected')),
    human_decided_at  TEXT,
    branch            TEXT,
    message           TEXT
);
CREATE INDEX runs_by_item ON runs (project_key, work_item_id);

CREATE TABLE project_locks (
    project_key TEXT    NOT NULL PRIMARY KEY,
    pid         INTEGER NOT NULL,
    owner       TEXT    NOT NULL,
    acquired_at TEXT    NOT NULL
);

CREATE TABLE schedules (
    project_key TEXT    NOT NULL PRIMARY KEY,
    at_time     TEXT    NOT NULL,
    run_limit   INTEGER,
    command     TEXT    NOT NULL,
    created_at  TEXT    NOT NULL
);
