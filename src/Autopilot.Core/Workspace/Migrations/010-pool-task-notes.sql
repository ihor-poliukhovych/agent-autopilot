-- Notes the pool master leaves for the next attempt at a task whose text lives in a tracker (the workspace cannot
-- edit it). The work item id is the tracker's id.
CREATE TABLE IF NOT EXISTS pool_task_notes (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    project_key  TEXT NOT NULL,
    work_item_id TEXT NOT NULL,
    note         TEXT NOT NULL,
    created_at   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_pool_task_notes_item ON pool_task_notes (project_key, work_item_id);
