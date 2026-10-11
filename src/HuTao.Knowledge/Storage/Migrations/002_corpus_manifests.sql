CREATE TABLE corpus_manifests (
 namespace_id TEXT NOT NULL REFERENCES namespaces(namespace_id), corpus_version TEXT NOT NULL,
 stats_json TEXT NOT NULL,
 PRIMARY KEY(namespace_id,corpus_version));
