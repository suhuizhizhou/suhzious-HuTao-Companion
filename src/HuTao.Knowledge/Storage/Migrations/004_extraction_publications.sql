CREATE TABLE extraction_publications (
 job_id TEXT PRIMARY KEY REFERENCES extraction_jobs(job_id),
 decision_hash TEXT NOT NULL, reviewer_id TEXT NOT NULL, published_at INTEGER NOT NULL, assertion_ids_json TEXT NOT NULL,
 retracted_at INTEGER);
