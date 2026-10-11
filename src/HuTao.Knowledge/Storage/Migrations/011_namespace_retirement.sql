-- Durable namespace handoff fence. Not a security boundary against a database administrator.
CREATE TABLE namespace_retirements (
 namespace_id TEXT PRIMARY KEY REFERENCES namespaces(namespace_id),
 installation_id TEXT NOT NULL CHECK(length(installation_id)>0));
CREATE TRIGGER retired_namespaces_insert BEFORE INSERT ON namespaces
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_namespaces_update BEFORE UPDATE ON namespaces
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_namespaces_delete BEFORE DELETE ON namespaces
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_namespace_state_insert BEFORE INSERT ON namespace_state
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_namespace_state_update BEFORE UPDATE ON namespace_state
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_namespace_state_delete BEFORE DELETE ON namespace_state
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_sources_insert BEFORE INSERT ON sources
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_sources_update BEFORE UPDATE ON sources
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_sources_delete BEFORE DELETE ON sources
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_source_revisions_insert BEFORE INSERT ON source_revisions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_source_revisions_update BEFORE UPDATE ON source_revisions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_source_revisions_delete BEFORE DELETE ON source_revisions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_evidence_units_insert BEFORE INSERT ON evidence_units
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_evidence_units_update BEFORE UPDATE ON evidence_units
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_evidence_units_delete BEFORE DELETE ON evidence_units
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_evidence_search_map_insert BEFORE INSERT ON evidence_search_map
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_evidence_search_map_update BEFORE UPDATE ON evidence_search_map
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_evidence_search_map_delete BEFORE DELETE ON evidence_search_map
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_memory_records_insert BEFORE INSERT ON memory_records
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_memory_records_update BEFORE UPDATE ON memory_records
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_memory_records_delete BEFORE DELETE ON memory_records
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_memory_derivations_insert BEFORE INSERT ON memory_derivations
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_memory_derivations_update BEFORE UPDATE ON memory_derivations
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_memory_derivations_delete BEFORE DELETE ON memory_derivations
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_memory_state_insert BEFORE INSERT ON memory_state
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_memory_state_update BEFORE UPDATE ON memory_state
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_memory_state_delete BEFORE DELETE ON memory_state
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_important_items_insert BEFORE INSERT ON important_items
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_important_items_update BEFORE UPDATE ON important_items
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_important_items_delete BEFORE DELETE ON important_items
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_entities_insert BEFORE INSERT ON entities
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_entities_update BEFORE UPDATE ON entities
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_entities_delete BEFORE DELETE ON entities
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_entity_aliases_insert BEFORE INSERT ON entity_aliases
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_entity_aliases_update BEFORE UPDATE ON entity_aliases
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_entity_aliases_delete BEFORE DELETE ON entity_aliases
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_mentions_insert BEFORE INSERT ON mentions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_mentions_update BEFORE UPDATE ON mentions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_mentions_delete BEFORE DELETE ON mentions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_assertions_insert BEFORE INSERT ON assertions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_assertions_update BEFORE UPDATE ON assertions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_assertions_delete BEFORE DELETE ON assertions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_assertion_evidence_insert BEFORE INSERT ON assertion_evidence
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_assertion_evidence_update BEFORE UPDATE ON assertion_evidence
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_assertion_evidence_delete BEFORE DELETE ON assertion_evidence
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_consolidation_runs_insert BEFORE INSERT ON consolidation_runs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_consolidation_runs_update BEFORE UPDATE ON consolidation_runs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_consolidation_runs_delete BEFORE DELETE ON consolidation_runs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_import_runs_insert BEFORE INSERT ON import_runs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_import_runs_update BEFORE UPDATE ON import_runs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_import_runs_delete BEFORE DELETE ON import_runs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_graph_outbox_insert BEFORE INSERT ON graph_outbox
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_graph_outbox_update BEFORE UPDATE ON graph_outbox
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_graph_outbox_delete BEFORE DELETE ON graph_outbox
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_deletion_tombstones_insert BEFORE INSERT ON deletion_tombstones
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_deletion_tombstones_update BEFORE UPDATE ON deletion_tombstones
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_deletion_tombstones_delete BEFORE DELETE ON deletion_tombstones
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_corpus_manifests_insert BEFORE INSERT ON corpus_manifests
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_corpus_manifests_update BEFORE UPDATE ON corpus_manifests
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_corpus_manifests_delete BEFORE DELETE ON corpus_manifests
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_extraction_jobs_insert BEFORE INSERT ON extraction_jobs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_extraction_jobs_update BEFORE UPDATE ON extraction_jobs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_extraction_jobs_delete BEFORE DELETE ON extraction_jobs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_graph_rebuild_jobs_insert BEFORE INSERT ON graph_rebuild_jobs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_graph_rebuild_jobs_update BEFORE UPDATE ON graph_rebuild_jobs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_graph_rebuild_jobs_delete BEFORE DELETE ON graph_rebuild_jobs
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_graph_rebuild_items_insert BEFORE INSERT ON graph_rebuild_items
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_graph_rebuild_items_update BEFORE UPDATE ON graph_rebuild_items
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_graph_rebuild_items_delete BEFORE DELETE ON graph_rebuild_items
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_turn_ingestion_insert BEFORE INSERT ON turn_ingestion
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_turn_ingestion_update BEFORE UPDATE ON turn_ingestion
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_turn_ingestion_delete BEFORE DELETE ON turn_ingestion
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_recovery_adoptions_insert BEFORE INSERT ON recovery_adoptions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_recovery_adoptions_update BEFORE UPDATE ON recovery_adoptions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id OR namespace_id=NEW.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_recovery_adoptions_delete BEFORE DELETE ON recovery_adoptions
 WHEN EXISTS(SELECT 1 FROM namespace_retirements WHERE namespace_id=OLD.namespace_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_publications_insert BEFORE INSERT ON extraction_publications
 WHEN EXISTS(SELECT 1 FROM extraction_jobs j JOIN namespace_retirements r USING(namespace_id) WHERE j.job_id=NEW.job_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_publications_update BEFORE UPDATE ON extraction_publications
 WHEN EXISTS(SELECT 1 FROM extraction_jobs j JOIN namespace_retirements r USING(namespace_id) WHERE j.job_id=OLD.job_id OR j.job_id=NEW.job_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;
CREATE TRIGGER retired_publications_delete BEFORE DELETE ON extraction_publications
 WHEN EXISTS(SELECT 1 FROM extraction_jobs j JOIN namespace_retirements r USING(namespace_id) WHERE j.job_id=OLD.job_id)
 BEGIN SELECT RAISE(ABORT,'Namespace SQL authority retired'); END;

