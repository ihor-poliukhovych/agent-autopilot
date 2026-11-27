-- What the pool master decided, so a person can see and undo it.
CREATE TABLE pool_decisions (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    project_key  TEXT    NOT NULL,
    work_item_id TEXT    NOT NULL,
    kind         TEXT    NOT NULL CHECK (kind IN ('answer', 'failure')),
    question     TEXT    NOT NULL,
    answer       TEXT    NOT NULL,
    reason       TEXT    NOT NULL,
    category     TEXT,
    created_at   TEXT    NOT NULL,
    reverted_at  TEXT
);
CREATE INDEX pool_decisions_by_project ON pool_decisions (project_key, created_at);

-- Tasks the pool left for a person. The pool goes on with the next task.
CREATE TABLE pool_parked (
    project_key  TEXT NOT NULL,
    work_item_id TEXT NOT NULL,
    category     TEXT,
    reason       TEXT NOT NULL,
    parked_at    TEXT NOT NULL,
    PRIMARY KEY (project_key, work_item_id)
);

-- One row per task the running pool is working on or holding back, for the Pool page.
CREATE TABLE pool_status (
    project_key  TEXT NOT NULL,
    work_item_id TEXT NOT NULL,
    state        TEXT NOT NULL CHECK (state IN ('running', 'waiting')),
    detail       TEXT,
    since        TEXT NOT NULL,
    PRIMARY KEY (project_key, work_item_id)
);

-- The pool is paused (infrastructure failure, budget) until retry_at.
CREATE TABLE pool_pause (
    project_key TEXT NOT NULL PRIMARY KEY,
    reason      TEXT NOT NULL,
    retry_at    TEXT,
    created_at  TEXT NOT NULL
);

-- One row per agent run, for the budget (a rolling 24 hours).
CREATE TABLE pool_agent_runs (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    project_key  TEXT NOT NULL,
    work_item_id TEXT,
    kind         TEXT NOT NULL,
    started_at   TEXT NOT NULL
);
CREATE INDEX pool_agent_runs_by_project ON pool_agent_runs (project_key, started_at);

-- One lock per task, so two agents never work on the same task.
CREATE TABLE task_locks (
    project_key  TEXT    NOT NULL,
    work_item_id TEXT    NOT NULL,
    pid          INTEGER NOT NULL,
    owner        TEXT    NOT NULL,
    acquired_at  TEXT    NOT NULL,
    PRIMARY KEY (project_key, work_item_id)
);

-- The integration branch of a project and what the pool merged into it.
CREATE TABLE pool_integration (
    project_key  TEXT NOT NULL PRIMARY KEY,
    branch       TEXT NOT NULL,
    base_branch  TEXT NOT NULL,
    created_at   TEXT NOT NULL,
    final_pr_url TEXT
);

CREATE TABLE pool_integration_merges (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    project_key  TEXT NOT NULL,
    branch       TEXT NOT NULL,
    work_item_id TEXT NOT NULL,
    task_branch  TEXT NOT NULL,
    commit_sha   TEXT NOT NULL,
    merged_at    TEXT NOT NULL
);

-- A schedule can start on a date, repeat on some days and stop at a time.
ALTER TABLE schedules ADD COLUMN start_date TEXT;
ALTER TABLE schedules ADD COLUMN repeat TEXT;
ALTER TABLE schedules ADD COLUMN until_time TEXT;
ALTER TABLE schedules ADD COLUMN unattended INTEGER;
ALTER TABLE schedules ADD COLUMN last_started_at TEXT;
