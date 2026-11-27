-- A running pool beats every few seconds. A slot or a lock whose owner has not beaten for a few minutes
-- belongs to a run that died, and is released by the next run (or by the status page).
CREATE TABLE IF NOT EXISTS pool_heartbeats (
    project_key TEXT    NOT NULL,
    pid         INTEGER NOT NULL,
    beat_at     TEXT    NOT NULL,
    PRIMARY KEY (project_key, pid)
);

-- A slot can also be 'interrupted': the run that owned it died. The task stays in the pool.
CREATE TABLE pool_status_new (
    project_key  TEXT NOT NULL,
    work_item_id TEXT NOT NULL,
    state        TEXT NOT NULL CHECK (state IN ('running', 'waiting', 'interrupted')),
    detail       TEXT,
    since        TEXT NOT NULL,
    PRIMARY KEY (project_key, work_item_id)
);
INSERT INTO pool_status_new (project_key, work_item_id, state, detail, since)
    SELECT project_key, work_item_id, state, detail, since FROM pool_status;
DROP TABLE pool_status;
ALTER TABLE pool_status_new RENAME TO pool_status;
