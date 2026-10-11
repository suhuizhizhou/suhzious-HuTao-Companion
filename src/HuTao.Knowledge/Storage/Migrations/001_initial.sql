CREATE TABLE namespaces (
 namespace_id TEXT PRIMARY KEY, domain TEXT NOT NULL CHECK(domain IN ('corpus','memory')),
 active_corpus_version TEXT, privacy_epoch INTEGER NOT NULL DEFAULT 0 CHECK(privacy_epoch>=0));
CREATE TABLE namespace_state (
 namespace_id TEXT PRIMARY KEY REFERENCES namespaces(namespace_id), commit_seq INTEGER NOT NULL DEFAULT 0,
 graph_generation TEXT NOT NULL DEFAULT 'initial', graph_checkpoint INTEGER NOT NULL DEFAULT 0);
CREATE TABLE sources (
 namespace_id TEXT NOT NULL REFERENCES namespaces(namespace_id), source_id TEXT NOT NULL,
 kind TEXT NOT NULL, locator TEXT NOT NULL, authority TEXT NOT NULL, license_ref TEXT,
 PRIMARY KEY(namespace_id,source_id));
CREATE TABLE source_revisions (
 namespace_id TEXT NOT NULL, source_id TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>0),
 content_hash TEXT NOT NULL, corpus_version TEXT NOT NULL, recorded_at INTEGER NOT NULL, retired_at INTEGER,
 PRIMARY KEY(namespace_id,source_id,revision), FOREIGN KEY(namespace_id,source_id) REFERENCES sources(namespace_id,source_id));
CREATE TABLE evidence_units (
 namespace_id TEXT NOT NULL, evidence_id TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>0),
 source_id TEXT NOT NULL, source_revision INTEGER NOT NULL, text TEXT NOT NULL, text_hash TEXT NOT NULL,
 speaker TEXT NOT NULL, chapter_id TEXT NOT NULL, sequence INTEGER NOT NULL DEFAULT 0, variant INTEGER NOT NULL DEFAULT 0,
 source_kind TEXT NOT NULL, observed_at INTEGER, recorded_at INTEGER NOT NULL, deleted_at INTEGER,
 corpus_version TEXT NOT NULL, metadata_json TEXT NOT NULL DEFAULT '{}',
 PRIMARY KEY(namespace_id,evidence_id,revision),
 FOREIGN KEY(namespace_id,source_id,source_revision) REFERENCES source_revisions(namespace_id,source_id,revision));
CREATE INDEX evidence_chapter ON evidence_units(namespace_id,corpus_version,chapter_id,sequence,variant);
CREATE INDEX evidence_source ON evidence_units(namespace_id,source_id,source_revision);
CREATE TABLE evidence_search_map (
 rowid INTEGER PRIMARY KEY, namespace_id TEXT NOT NULL, evidence_id TEXT NOT NULL, revision INTEGER NOT NULL,
 UNIQUE(namespace_id,evidence_id,revision),
 FOREIGN KEY(namespace_id,evidence_id,revision) REFERENCES evidence_units(namespace_id,evidence_id,revision));
CREATE VIRTUAL TABLE evidence_fts USING fts5(tokens, tokenize='unicode61');
CREATE TABLE memory_records (
 namespace_id TEXT NOT NULL, memory_id TEXT NOT NULL, evidence_revision INTEGER NOT NULL,
 kind TEXT NOT NULL, importance REAL NOT NULL CHECK(importance BETWEEN 0 AND 1), fingerprint TEXT NOT NULL,
 valid_from INTEGER, valid_to INTEGER, superseded_by TEXT, access_count INTEGER NOT NULL DEFAULT 0,
 last_accessed_at INTEGER, consolidated_at INTEGER, archived INTEGER NOT NULL DEFAULT 0 CHECK(archived IN (0,1)),
 PRIMARY KEY(namespace_id,memory_id),
 FOREIGN KEY(namespace_id,memory_id,evidence_revision) REFERENCES evidence_units(namespace_id,evidence_id,revision),
 FOREIGN KEY(namespace_id,superseded_by) REFERENCES memory_records(namespace_id,memory_id) DEFERRABLE INITIALLY DEFERRED);
CREATE INDEX memory_fingerprint ON memory_records(namespace_id,fingerprint);
CREATE TABLE memory_derivations (
 namespace_id TEXT NOT NULL, memory_id TEXT NOT NULL, memory_revision INTEGER NOT NULL,
 source_evidence_id TEXT NOT NULL, source_revision INTEGER NOT NULL,
 PRIMARY KEY(namespace_id,memory_id,memory_revision,source_evidence_id,source_revision),
 FOREIGN KEY(namespace_id,memory_id,memory_revision) REFERENCES evidence_units(namespace_id,evidence_id,revision),
 FOREIGN KEY(namespace_id,source_evidence_id,source_revision) REFERENCES evidence_units(namespace_id,evidence_id,revision));
CREATE TABLE memory_state (
 namespace_id TEXT PRIMARY KEY REFERENCES namespaces(namespace_id), imported_chat_entries INTEGER NOT NULL DEFAULT 0,
 last_consolidated_at INTEGER, state_revision INTEGER NOT NULL DEFAULT 0);
CREATE TABLE important_items (
 namespace_id TEXT NOT NULL, item_id TEXT NOT NULL, evidence_id TEXT NOT NULL, evidence_revision INTEGER NOT NULL,
 due_at INTEGER, last_offered_at INTEGER, status TEXT NOT NULL,
 PRIMARY KEY(namespace_id,item_id),
 FOREIGN KEY(namespace_id,evidence_id,evidence_revision) REFERENCES evidence_units(namespace_id,evidence_id,revision));
CREATE TABLE entities (
 namespace_id TEXT NOT NULL REFERENCES namespaces(namespace_id), entity_id TEXT NOT NULL, type TEXT NOT NULL,
 canonical_name TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'active', revision INTEGER NOT NULL DEFAULT 1,
 PRIMARY KEY(namespace_id,entity_id));
CREATE TABLE entity_aliases (
 namespace_id TEXT NOT NULL, normalized_alias TEXT NOT NULL, alias TEXT NOT NULL, entity_id TEXT NOT NULL,
 source_evidence_id TEXT NOT NULL, source_revision INTEGER NOT NULL,
 PRIMARY KEY(namespace_id,normalized_alias,entity_id,source_evidence_id,source_revision),
 FOREIGN KEY(namespace_id,entity_id) REFERENCES entities(namespace_id,entity_id),
 FOREIGN KEY(namespace_id,source_evidence_id,source_revision) REFERENCES evidence_units(namespace_id,evidence_id,revision));
CREATE TABLE mentions (
 namespace_id TEXT NOT NULL, evidence_id TEXT NOT NULL, evidence_revision INTEGER NOT NULL, entity_id TEXT NOT NULL,
 start_utf16 INTEGER NOT NULL CHECK(start_utf16>=0), end_utf16 INTEGER NOT NULL CHECK(end_utf16>start_utf16),
 PRIMARY KEY(namespace_id,evidence_id,evidence_revision,entity_id,start_utf16),
 FOREIGN KEY(namespace_id,entity_id) REFERENCES entities(namespace_id,entity_id),
 FOREIGN KEY(namespace_id,evidence_id,evidence_revision) REFERENCES evidence_units(namespace_id,evidence_id,revision));
CREATE TABLE predicates (
 predicate_id TEXT PRIMARY KEY, subject_types TEXT NOT NULL, object_types TEXT NOT NULL,
 symmetric INTEGER NOT NULL DEFAULT 0, functional INTEGER NOT NULL DEFAULT 0,
 allows_negation INTEGER NOT NULL DEFAULT 1, inference_rule_id TEXT);
CREATE TABLE assertions (
 namespace_id TEXT NOT NULL, assertion_id TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>0),
 subject_id TEXT NOT NULL, predicate_id TEXT NOT NULL REFERENCES predicates(predicate_id), object_id TEXT, typed_literal TEXT,
 polarity INTEGER NOT NULL CHECK(polarity IN (-1,1)), qualifiers_json TEXT NOT NULL DEFAULT '{}',
 valid_from INTEGER, valid_to INTEGER, time_basis TEXT NOT NULL DEFAULT 'unknown',
 status TEXT NOT NULL CHECK(status IN ('candidate','accepted','disputed','superseded','retracted')),
 extraction_confidence REAL NOT NULL CHECK(extraction_confidence BETWEEN 0 AND 1), validation_method TEXT NOT NULL,
 superseded_by TEXT, recorded_at INTEGER NOT NULL, retired_at INTEGER,
 PRIMARY KEY(namespace_id,assertion_id,revision), CHECK((object_id IS NULL)<>(typed_literal IS NULL)),
 CHECK(valid_from IS NULL OR valid_to IS NULL OR valid_from<valid_to),
 FOREIGN KEY(namespace_id,subject_id) REFERENCES entities(namespace_id,entity_id),
 FOREIGN KEY(namespace_id,object_id) REFERENCES entities(namespace_id,entity_id));
CREATE INDEX assertion_subject ON assertions(namespace_id,subject_id,predicate_id,status,retired_at);
CREATE INDEX assertion_object ON assertions(namespace_id,object_id,predicate_id,status,retired_at);
CREATE TABLE assertion_evidence (
 namespace_id TEXT NOT NULL, assertion_id TEXT NOT NULL, assertion_revision INTEGER NOT NULL,
 evidence_id TEXT NOT NULL, evidence_revision INTEGER NOT NULL,
 start_utf16 INTEGER NOT NULL CHECK(start_utf16>=0), end_utf16 INTEGER NOT NULL CHECK(end_utf16>start_utf16),
 support_kind TEXT NOT NULL CHECK(support_kind IN ('supports','contradicts')),
 PRIMARY KEY(namespace_id,assertion_id,assertion_revision,evidence_id,evidence_revision,start_utf16),
 FOREIGN KEY(namespace_id,assertion_id,assertion_revision) REFERENCES assertions(namespace_id,assertion_id,revision),
 FOREIGN KEY(namespace_id,evidence_id,evidence_revision) REFERENCES evidence_units(namespace_id,evidence_id,revision));
CREATE TABLE consolidation_runs (
 run_id TEXT PRIMARY KEY, namespace_id TEXT NOT NULL REFERENCES namespaces(namespace_id), input_hash TEXT NOT NULL,
 model TEXT NOT NULL, prompt_version TEXT NOT NULL, status TEXT NOT NULL, processed_cursor TEXT,
 UNIQUE(namespace_id,input_hash,prompt_version));
CREATE TABLE import_runs (
 run_id TEXT PRIMARY KEY, namespace_id TEXT NOT NULL REFERENCES namespaces(namespace_id), manifest_hash TEXT NOT NULL,
 parser_version TEXT NOT NULL, counts INTEGER NOT NULL, validation_status TEXT NOT NULL, committed_at INTEGER,
 UNIQUE(namespace_id,manifest_hash,parser_version));
CREATE TABLE graph_outbox (
 event_id TEXT PRIMARY KEY, namespace_id TEXT NOT NULL REFERENCES namespaces(namespace_id), commit_seq INTEGER NOT NULL,
 aggregate_id TEXT NOT NULL, aggregate_revision INTEGER NOT NULL, kind TEXT NOT NULL, generation TEXT NOT NULL,
 payload_hash TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, lease_until INTEGER, delivered_at INTEGER,
 UNIQUE(namespace_id,commit_seq,aggregate_id));
CREATE INDEX outbox_pending ON graph_outbox(namespace_id,delivered_at,commit_seq);
CREATE TABLE deletion_tombstones (
 namespace_id TEXT NOT NULL REFERENCES namespaces(namespace_id), evidence_id TEXT NOT NULL, deleted_at INTEGER NOT NULL,
 PRIMARY KEY(namespace_id,evidence_id));
