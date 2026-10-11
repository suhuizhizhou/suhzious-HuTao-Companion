CREATE TABLE graph_rebuild_jobs (
 namespace_id TEXT NOT NULL REFERENCES namespaces(namespace_id), generation TEXT NOT NULL,
 source_generation TEXT NOT NULL, snapshot_seq INTEGER NOT NULL, privacy_epoch INTEGER NOT NULL,
 status TEXT NOT NULL CHECK(status IN ('ready','running','interrupted','activated','invalidated','failed')),
 lease_token TEXT, lease_until INTEGER, last_error TEXT, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
 PRIMARY KEY(namespace_id,generation));
CREATE TABLE graph_rebuild_items (
 namespace_id TEXT NOT NULL, generation TEXT NOT NULL, assertion_id TEXT NOT NULL,
 revision INTEGER NOT NULL, snapshot_json TEXT NOT NULL, projected_revision INTEGER NOT NULL DEFAULT 0,
 PRIMARY KEY(namespace_id,generation,assertion_id),
 FOREIGN KEY(namespace_id,generation) REFERENCES graph_rebuild_jobs(namespace_id,generation) ON DELETE CASCADE);
CREATE INDEX graph_rebuild_pending ON graph_rebuild_items(namespace_id,generation,projected_revision,revision);
