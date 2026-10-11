CREATE TABLE recovery_adoptions (
 decision_id TEXT PRIMARY KEY,
 namespace_id TEXT NOT NULL REFERENCES namespaces(namespace_id),
 reviewer_id TEXT NOT NULL,
 selected_ids_json TEXT NOT NULL,
 adopted_at INTEGER NOT NULL);
