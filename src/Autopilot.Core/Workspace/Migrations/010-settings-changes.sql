-- Changes to settings that move a review check, kept next to the run history. Append only.
CREATE TABLE IF NOT EXISTS settings_changes (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    project_key TEXT NOT NULL,
    at          TEXT NOT NULL,
    field       TEXT NOT NULL,
    old_value   TEXT,
    new_value   TEXT
);
CREATE INDEX IF NOT EXISTS ix_settings_changes_project ON settings_changes (project_key);
