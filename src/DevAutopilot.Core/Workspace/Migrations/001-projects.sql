CREATE TABLE projects (
    key               TEXT NOT NULL PRIMARY KEY,
    display_name      TEXT NOT NULL,
    repository_folder TEXT NOT NULL,
    base_branch       TEXT NOT NULL,
    task_source       TEXT NOT NULL CHECK (task_source IN ('local', 'azure-devops')),
    settings_json     TEXT NOT NULL DEFAULT '{}',
    created_at        TEXT NOT NULL
);
