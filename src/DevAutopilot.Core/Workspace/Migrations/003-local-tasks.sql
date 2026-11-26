CREATE TABLE tasks (
    project_key  TEXT    NOT NULL,
    number       INTEGER NOT NULL,
    title        TEXT    NOT NULL,
    body         TEXT    NOT NULL DEFAULT '',
    state        TEXT    NOT NULL DEFAULT 'open' CHECK (state IN ('open', 'closed')),
    labels_json  TEXT    NOT NULL DEFAULT '[]',
    external_id  TEXT,
    external_url TEXT,
    author       TEXT    NOT NULL,
    created_at   TEXT    NOT NULL,
    updated_at   TEXT    NOT NULL,
    closed_at    TEXT,
    PRIMARY KEY (project_key, number)
);
CREATE UNIQUE INDEX tasks_by_external ON tasks (project_key, external_id) WHERE external_id IS NOT NULL;

CREATE TABLE task_comments (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    project_key TEXT NOT NULL,
    task_number INTEGER NOT NULL,
    author      TEXT NOT NULL,
    body        TEXT NOT NULL,
    created_at  TEXT NOT NULL,
    synced_at   TEXT,
    FOREIGN KEY (project_key, task_number) REFERENCES tasks (project_key, number) ON DELETE CASCADE
);
CREATE INDEX task_comments_by_task ON task_comments (project_key, task_number);

CREATE TABLE task_history (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    project_key TEXT NOT NULL,
    task_number INTEGER NOT NULL,
    kind        TEXT NOT NULL,
    summary     TEXT NOT NULL,
    detail      TEXT,
    created_at  TEXT NOT NULL,
    synced_at   TEXT,
    FOREIGN KEY (project_key, task_number) REFERENCES tasks (project_key, number) ON DELETE CASCADE
);
CREATE INDEX task_history_by_task ON task_history (project_key, task_number);
