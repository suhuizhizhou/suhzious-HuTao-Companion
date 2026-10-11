CREATE TABLE turn_ingestion (
 namespace_id TEXT NOT NULL,
 evidence_id TEXT NOT NULL,
 evidence_revision INTEGER NOT NULL,
 status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','processing','completed','deferred','failed','cancelled')),
 attempts INTEGER NOT NULL DEFAULT 0,
 lease_token TEXT,
 lease_until INTEGER,
 diagnostic TEXT,
 created_at INTEGER NOT NULL,
 PRIMARY KEY(namespace_id,evidence_id,evidence_revision),
 FOREIGN KEY(namespace_id,evidence_id,evidence_revision) REFERENCES evidence_units(namespace_id,evidence_id,revision)
);
CREATE INDEX turn_ingestion_pending ON turn_ingestion(namespace_id,status,created_at);
