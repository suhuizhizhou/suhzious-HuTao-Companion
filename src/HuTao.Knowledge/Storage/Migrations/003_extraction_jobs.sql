CREATE TABLE extraction_jobs (
 job_id TEXT PRIMARY KEY,
 namespace_id TEXT NOT NULL, evidence_id TEXT NOT NULL, evidence_revision INTEGER NOT NULL,
 input_hash TEXT NOT NULL, extractor_version TEXT NOT NULL, validator_version TEXT NOT NULL, model TEXT NOT NULL,
 status TEXT NOT NULL CHECK(status IN ('started','completed','failed','interrupted')),
 started_at INTEGER NOT NULL, finished_at INTEGER, elapsed_ms INTEGER,
 input_characters INTEGER NOT NULL, output_json TEXT, error_code TEXT,
 prompt_tokens INTEGER, completion_tokens INTEGER,
 FOREIGN KEY(namespace_id,evidence_id,evidence_revision) REFERENCES evidence_units(namespace_id,evidence_id,revision));
CREATE INDEX extraction_evidence ON extraction_jobs(namespace_id,evidence_id,evidence_revision);
