ALTER TABLE graph_outbox ADD COLUMN lease_token TEXT;
ALTER TABLE graph_outbox ADD COLUMN next_attempt_at INTEGER NOT NULL DEFAULT 0;
ALTER TABLE graph_outbox ADD COLUMN diagnostic INTEGER NOT NULL DEFAULT 0 CHECK(diagnostic IN (0,1));
ALTER TABLE graph_outbox ADD COLUMN last_error TEXT;
CREATE INDEX outbox_claim ON graph_outbox(namespace_id,diagnostic,delivered_at,commit_seq);
