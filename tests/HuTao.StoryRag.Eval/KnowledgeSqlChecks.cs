using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using Microsoft.Data.Sqlite;
using HuTao.Knowledge.Import;
using HuTao.Knowledge.Rag;
using HuTao.Knowledge.Memory;

internal static class KnowledgeSqlChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var results = new List<CheckResult>();
        void Check(string name, bool ok) => results.Add(new("knowledge_" + name, ok, ""));
        void Reject(string name, Action action)
        { try { action(); Check(name,false); } catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or SqliteException or InvalidDataException) { Check(name,true); } }
        var root = Path.Combine(Path.GetTempPath(), "hutao-knowledge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var access = KnowledgeAccessContext.ForSession("test", "hutao", "desktop", ["corpus:test:canonical"]);
            access.Demand("corpus:test:canonical");
            access.Demand("memory:test:hutao:desktop");
            Reject("chatroom_isolated", () => access.Demand("memory:test:hutao:chatroom"));
            Reject("persona_isolated", () => access.Demand("memory:test:klee:desktop"));
            Reject("namespace_injection", () => KnowledgeAccessContext.ForSession("x:other", "hutao", "desktop", []));
            Reject("private_cannot_be_public_grant", () => KnowledgeAccessContext.ForSession("x", "hutao", "desktop", ["memory:y:hutao:desktop"]));
            Reject("revision_positive", () => new EvidenceRef("a", "b", 0));
            KnowledgePlanValidator.Validate([new("a", KnowledgeStrategy.SqlFacts, "x", [], []), new("b", KnowledgeStrategy.GraphExpand,"y",["a"],[])]);
            Reject("cyclic_plan", () => KnowledgePlanValidator.Validate([new("a",KnowledgeStrategy.SqlFacts,"x",["b"],[]),new("b",KnowledgeStrategy.SqlFacts,"y",["a"],[])]));
            Reject("missing_dependency", () => KnowledgePlanValidator.Validate([new("a",KnowledgeStrategy.SqlFacts,"x",["missing"],[])]));
            Reject("unknown_strategy", () => KnowledgePlanValidator.Validate([new("a",(KnowledgeStrategy)999,"x",[],[])]));
            var db = new SqliteKnowledgeDatabase(Path.Combine(root,"knowledge.db"));
            db.EnsureNamespace(access.MemoryNamespace);
            using (var connection = db.Open())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT sqlite_version()";
                results.Add(new("knowledge_sqlite_version",true,(string)command.ExecuteScalar()!));
                command.CommandText = "PRAGMA foreign_keys"; Check("foreign_keys_enabled", (long)command.ExecuteScalar()! == 1);
                command.CommandText = "PRAGMA journal_mode"; Check("wal_enabled", (string)command.ExecuteScalar()! == "wal");
                command.CommandText = "INSERT INTO evidence_fts(rowid,tokens) VALUES(1,'温水 饮品'); SELECT count(*) FROM evidence_fts WHERE evidence_fts MATCH '温水'";
                Check("real_fts5_search", (long)command.ExecuteScalar()! == 1);
                command.CommandText = "DELETE FROM evidence_fts"; command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO entities(namespace_id,entity_id,type,canonical_name) VALUES('missing','x','person','x')";
                Reject("namespace_foreign_key", () => command.ExecuteNonQuery());
            }
            _ = new SqliteKnowledgeDatabase(db.FilePath);
            Check("migration_idempotent", true);
            using(var connection=db.Open())
            {
                using var command=connection.CreateCommand();
                command.CommandText="SELECT s.name FROM sqlite_schema s WHERE s.type='table' AND s.name<>'namespace_retirements' AND EXISTS(SELECT 1 FROM pragma_table_info(s.name) WHERE name='namespace_id')";
                var scopedTables=new List<string>();using(var rows=command.ExecuteReader())while(rows.Read())scopedTables.Add(rows.GetString(0));
                var covered=true;
                foreach(var table in scopedTables)
                {
                    command.CommandText="SELECT COUNT(*) FROM sqlite_schema WHERE type='trigger' AND tbl_name=@table AND name IN (@i,@u,@d)";
                    command.Parameters.Clear();command.Parameters.AddWithValue("@table",table);
                    foreach(var operation in new[]{("@i","insert"),("@u","update"),("@d","delete")})command.Parameters.AddWithValue(operation.Item1,"retired_"+table+"_"+operation.Item2);
                    covered &= Convert.ToInt32(command.ExecuteScalar())==3;
                }
                Check("retirement_covers_all_namespace_tables",covered && scopedTables.Count>=25);
            }
            var upgradePath=Path.Combine(root,"upgrade.db");
            _=new SqliteKnowledgeDatabase(upgradePath);
            using(var old=new SqliteConnection($"Data Source={upgradePath};Pooling=False"))
            {
                old.Open(); using var command=old.CreateCommand();
                var retireTriggers=new List<string>();
                command.CommandText="SELECT name FROM sqlite_schema WHERE type='trigger' AND name LIKE 'retired_%'";
                using(var triggerRows=command.ExecuteReader())while(triggerRows.Read())retireTriggers.Add(triggerRows.GetString(0));
                foreach(var trigger in retireTriggers){command.CommandText="DROP TRIGGER \""+trigger+"\"";command.ExecuteNonQuery();}
                command.CommandText="DROP TABLE namespace_retirements; DROP TABLE recovery_adoptions; DROP TABLE database_identity; DROP TABLE recovery_gate; DROP TABLE turn_ingestion; DROP TABLE graph_rebuild_items; DROP TABLE graph_rebuild_jobs; DROP INDEX outbox_claim; ALTER TABLE graph_outbox DROP COLUMN lease_token; ALTER TABLE graph_outbox DROP COLUMN next_attempt_at; ALTER TABLE graph_outbox DROP COLUMN diagnostic; ALTER TABLE graph_outbox DROP COLUMN last_error; DROP TABLE extraction_publications; DROP TABLE extraction_jobs; DROP TABLE corpus_manifests; DELETE FROM schema_migrations WHERE version>=2"; command.ExecuteNonQuery();
            }
            var upgraded=new SqliteKnowledgeDatabase(upgradePath);
            using(var c=upgraded.Open())
            { using var cmd=c.CreateCommand(); cmd.CommandText="SELECT MAX(version) FROM schema_migrations"; Check("real_v1_to_current_upgrade",Convert.ToInt64(cmd.ExecuteScalar())==11); }
            var brokenPath=Path.Combine(root,"broken-upgrade.db");
            _=new SqliteKnowledgeDatabase(brokenPath);
            using(var old=new SqliteConnection($"Data Source={brokenPath};Pooling=False"))
            {
                old.Open(); using var cmd=old.CreateCommand();cmd.CommandText="DELETE FROM schema_migrations WHERE version>=2";cmd.ExecuteNonQuery();
            }
            Reject("failed_upgrade_rejected",()=>new SqliteKnowledgeDatabase(brokenPath));
            using(var old=new SqliteConnection($"Data Source={brokenPath};Pooling=False"))
            {
                old.Open();using var cmd=old.CreateCommand();cmd.CommandText="SELECT MAX(version) FROM schema_migrations";
                Check("failed_upgrade_no_partial_version",Convert.ToInt64(cmd.ExecuteScalar())==1);
            }
            var repository = new SqlEvidenceRepository(db);
            var initial = repository.Put(access.MemoryNamespace,new("m1","我晚上只喝温水","user","turn:m1","turn"));
            var repeat = repository.Put(access.MemoryNamespace,new("m1","我晚上只喝温水","user","turn:m1","turn"));
            Check("idempotent_evidence",initial==repeat);
            Check("chinese_fts_recall",repository.Search(access,"晚上温水").Any(x=>x.Reference==initial));
            Check("hydrate_exact_text",repository.Read(access,initial)?.Text=="我晚上只喝温水");
            var revised=repository.Put(access.MemoryNamespace,new("m1","我晚上只喝白水","user","turn:m1","turn"));
            Check("stale_revision_rejected",revised.Revision>initial.Revision && repository.Read(access,initial) is null);
            Reject("hydration_scope_enforced",()=>repository.Read(access,new("memory:other:hutao:desktop","m1",1)));
            repository.Put("memory:other:hutao:desktop",new("secret","温水秘密","user","secret","turn"));
            Check("fts_operators_are_data",repository.Search(access,"\" OR * NEAR(温水) NOT - :").All(x=>access.Namespaces.Contains(x.Reference.Namespace)));
            Check("fts_punctuation_does_not_match_all",repository.Search(access,"\" * : ()").Count==0);
            var importer=new StorySqlImporter(db);
            var line=new StoryDialogueLine { ChapterId="chapter",LineId=1,Text="帽子上的梅花",Speaker="胡桃",SourceFile="chapter.json",Resolved=true };
            var report=importer.ImportLines("corpus:test:canonical",[line],"v1");
            Check("corpus_roundtrip_id",importer.ReadActiveLines(report.Namespace).Single().EvidenceId==line.EvidenceId);
            Check("corpus_import_idempotent",importer.ImportLines(report.Namespace,[line],"v1").AlreadyImported);
            StorySqlImporter.VerifyExisting(db.FilePath,report.Namespace,[line],"v1");
            Check("existing_corpus_readonly_verification",true);
            Reject("existing_corpus_wrong_version",()=>StorySqlImporter.VerifyExisting(db.FilePath,report.Namespace,[line],"missing"));
            Reject("existing_corpus_wrong_count",()=>StorySqlImporter.VerifyExisting(db.FilePath,report.Namespace,[],"v1"));
            var missingDatabase=Path.Combine(root,"must-not-create.db");
            Reject("existing_corpus_missing_database",()=>StorySqlImporter.VerifyExisting(missingDatabase,report.Namespace,[line],"v1"));
            Check("existing_corpus_missing_not_created",!File.Exists(missingDatabase));
            var found=repository.Search(access,"梅花").Single(x=>x.Reference.Namespace==report.Namespace);
            using(var cancelled=new CancellationTokenSource())
            { cancelled.Cancel(); try { importer.ImportLines(report.Namespace,[line],"v2",ct:cancelled.Token); } catch(OperationCanceledException) { } }
            Check("cancelled_import_keeps_active",repository.Read(access,found.Reference)?.Text==line.Text);
            var updated=new StoryDialogueLine { ChapterId="chapter",LineId=1,Text="帽子上的红花",Speaker="胡桃",SourceFile="chapter.json",Resolved=true };
            Reject("committed_version_content_collision",()=>importer.ImportLines(report.Namespace,[updated],"v1"));
            Reject("committed_version_count_collision",()=>importer.ImportLines(report.Namespace,[],"v1"));
            importer.ImportLines(report.Namespace,[updated],"v2");
            Check("activation_rejects_old_corpus",repository.Read(access,found.Reference) is null);
            Check("corpus_metadata_preserved",importer.ReadActiveLines(report.Namespace).Single().SourceFile==updated.SourceFile);
            Check("fts_stable_order",repository.Search(access,"晚上").SequenceEqual(repository.Search(access,"晚上")));
            var beforeRebuild=repository.Search(access,"晚上");
            repository.RebuildSearchIndex(access,access.MemoryNamespace);
            Check("fts_rebuild_equivalent",beforeRebuild.SequenceEqual(repository.Search(access,"晚上")));
            Reject("fts_rebuild_scope_enforced",()=>repository.RebuildSearchIndex(access,"memory:other:hutao:desktop"));
            Reject("duplicate_import_id_rejected",()=>importer.ImportLines(report.Namespace,[line,updated],"v3"));
            var memoryAccess=KnowledgeAccessContext.ForSession("memory-test","hutao","desktop",[]);
            var memoryRepository=new SqlConversationMemoryRepository(db,memoryAccess);
            var store=new ConversationMemoryStore(Path.Combine(root,"unused.json"),repository:memoryRepository);
            var now=DateTimeOffset.UtcNow;
            var turn=store.ObserveTurn("user","我喜欢晚上喝温水",now)!;
            Check("sql_memory_immediate_persistence",memoryRepository.Load().State.Records.Single().Id==turn.Id && !File.Exists(store.FilePath));
            try
            {
                store.CommitBatch(()=>{
                    store.AddConsolidated("用户晚上喝温水",MemoryKind.Preference,.8,[turn.Id],now,now,null,null);
                    store.MarkConsolidated([turn.Id],now);
                    throw new InvalidDataException("third fact rejected");
                });
            }
            catch(InvalidDataException) { }
            Check("memory_batch_rollback_in_memory",store.Count==1 && store.LastConsolidatedAt is null && store.FindById(turn.Id)?.ConsolidatedAt is null);
            Check("memory_batch_rollback_sql",memoryRepository.Load().State.Records.Count==1 && memoryRepository.Load().State.LastConsolidatedAt is null);
            store.CommitBatch(()=>{
                store.AddConsolidated("用户晚上喝温水",MemoryKind.Preference,.8,[turn.Id],now,now,null,null);
                store.MarkConsolidated([turn.Id],now);
            });
            var restarted=new ConversationMemoryStore(store.FilePath,repository:memoryRepository);
            Check("memory_restart_sources_cursor",restarted.Count==2 && restarted.LastConsolidatedAt is not null && restarted.Snapshot().Single(r=>r.Kind==MemoryKind.Preference).Sources.SequenceEqual([turn.Id]));
            var beforeInvalid=memoryRepository.Load();
            beforeInvalid.State.Records.Single(r=>r.Kind==MemoryKind.Preference).Sources.Add("missing-source");
            Reject("memory_dangling_source_rejected",()=>memoryRepository.Save(beforeInvalid.State,beforeInvalid.Revision));
            Check("memory_dangling_source_atomic",memoryRepository.Load().Revision==beforeInvalid.Revision);
            var stale=memoryRepository.Load();
            store.ObserveTurn("user","我明天继续做围巾",now.AddMinutes(1));
            var conflict=false;
            try { memoryRepository.Save(stale.State,stale.Revision); } catch(InvalidOperationException) { conflict=true; }
            Check("memory_stale_writer_no_lost_update",conflict && memoryRepository.Load().State.Records.Count==3);
            var assistant=store.ObserveTurn("assistant","建议你喝咖啡",now.AddMinutes(2))!;
            Reject("assistant_cannot_create_user_preference",()=>store.AddConsolidated("用户喜欢咖啡",MemoryKind.Preference,.8,[assistant.Id],now,now,null,null));
            Check("failed_write_restores_memory",store.Count==memoryRepository.Load().State.Records.Count);
            var exported=Path.Combine(root,"memory-export.json");
            var blockedLegacyPath=Path.Combine(root,"legacy-flush-blocked.json");
            Directory.CreateDirectory(blockedLegacyPath);
            var legacyPending=new ConversationMemoryStore(blockedLegacyPath);
            legacyPending.ObserveTurn("user","迁移前尚未保存的合成记忆",now);
            var strictFlushFailed=false;
            try {legacyPending.Flush(throwOnError:true);}catch(IOException){strictFlushFailed=true;}
            catch(UnauthorizedAccessException){strictFlushFailed=true;}
            Check("legacy_strict_flush_reports_failure",strictFlushFailed && legacyPending.Count==1);
            Directory.Delete(blockedLegacyPath);
            legacyPending.Flush(throwOnError:true);
            Check("legacy_strict_flush_retry_keeps_pending_memory",new ConversationMemoryStore(blockedLegacyPath).Snapshot().Single().Text=="迁移前尚未保存的合成记忆");
            new MemoryJsonImporter(memoryRepository).Export(exported);
            var originalExport=File.ReadAllBytes(exported);
            var existingRejected=false;
            try {new MemoryJsonImporter(memoryRepository).Export(exported);}catch(IOException){existingRejected=true;}
            Check("memory_export_preserves_existing_destination",existingRejected && File.ReadAllBytes(exported).SequenceEqual(originalExport) && !Directory.EnumerateFiles(root,".memory-export-*.tmp").Any());
            var badExport=Path.Combine(root,"failed-export.json");
            var invalidState=new MemoryFileState {Records=[new MemoryRecord {Id="bad",Importance=double.NaN}]};
            var failedSerialization=false;
            try {new MemoryJsonImporter(new ExportFixtureRepository(invalidState)).Export(badExport);}catch(ArgumentException){failedSerialization=true;}
            Check("memory_export_failure_leaves_no_partial_file",failedSerialization && !File.Exists(badExport) && !Directory.EnumerateFiles(root,".memory-export-*.tmp").Any());
            var importAccess=KnowledgeAccessContext.ForSession("import-test","hutao","desktop",[]);
            var targetMemory=new SqlConversationMemoryRepository(db,importAccess);
            var memoryImporter=new MemoryJsonImporter(targetMemory);
            Check("memory_dry_run_no_write",memoryImporter.Import(exported).Records==store.Count && targetMemory.Load().Revision==0);
            var reviewedImport=memoryImporter.Import(exported);
            var changedSource=Path.Combine(root,"memory-changed-after-review.json");
            File.WriteAllBytes(changedSource,File.ReadAllBytes(exported).Concat(new byte[]{32}).ToArray());
            var sourceChangedRejected=false;
            try {memoryImporter.Import(changedSource,false,reviewedImport.Sha256);}catch(InvalidDataException){sourceChangedRejected=true;}
            Check("memory_import_rejects_changed_reviewed_bytes",sourceChangedRejected && targetMemory.Load().Revision==0 && targetMemory.Load().State.Records.Count==0);
            memoryImporter.Import(exported,false,reviewedImport.Sha256);
            Check("memory_import_export_idempotent",memoryImporter.Import(exported,false).AlreadyImported && targetMemory.Load().State.Records.Count==store.Count);
            db.Write((c,t)=> {using var command=c.CreateCommand();command.Transaction=t;command.CommandText="UPDATE memory_records SET archived=1 WHERE namespace_id=$n";command.Parameters.AddWithValue("$n",importAccess.MemoryNamespace);command.ExecuteNonQuery();return 0;});
            var archiveRejected=false;
            try {memoryImporter.Export(Path.Combine(root,"archive-loss.json"));}catch(InvalidOperationException){archiveRejected=true;}
            Check("memory_export_refuses_archive_loss",archiveRejected && !File.Exists(Path.Combine(root,"archive-loss.json")));
            var archiveImportPath=Path.Combine(root,"archive-import.json");
            var archiveSidecar=Path.Combine(root,"archive-import.archive.jsonl");
            var archivedTurn=new MemoryRecord {Id="archive-source",Kind=MemoryKind.Turn,Speaker="user",Text="合成归档来源",ObservedAt=now,RecordedAt=now};
            var activeFact=new MemoryRecord {Id="archive-derived",Kind=MemoryKind.Fact,Speaker="user",Text="合成归档提炼",ObservedAt=now,RecordedAt=now,Sources=[archivedTurn.Id]};
            var archiveState=new MemoryFileState {Records=[activeFact],ImportedChatEntries=7,LastConsolidatedAt=now};
            File.WriteAllText(archiveImportPath,System.Text.Json.JsonSerializer.Serialize(archiveState,MemoryFileState.Json));
            File.WriteAllText(archiveSidecar,System.Text.Json.JsonSerializer.Serialize(archivedTurn,MemoryFileState.Json)+Environment.NewLine,System.Text.Encoding.UTF8);
            var archiveRepository=new SqlConversationMemoryRepository(db,KnowledgeAccessContext.ForSession("archive-import","hutao","desktop",[]));
            var archiveImporter=new MemoryJsonImporter(archiveRepository);
            var archiveReview=archiveImporter.Import(archiveImportPath);
            Check("memory_archive_dry_run_resolves_sources_without_writing",archiveReview.Records==2 && archiveReview.ArchivedRecords==1 && archiveReview.UnresolvedSourceIds.Count==0 && archiveRepository.Load().Revision==0);
            File.AppendAllText(archiveSidecar,Environment.NewLine);
            var archiveChanged=false;try{archiveImporter.Import(archiveImportPath,false,archiveReview.Sha256);}catch(InvalidDataException){archiveChanged=true;}
            Check("memory_archive_change_invalidates_review",archiveChanged && archiveRepository.Load().Revision==0);
            var archiveReviewedAgain=archiveImporter.Import(archiveImportPath);
            archiveImporter.Import(archiveImportPath,false,archiveReviewedAgain.Sha256);
            var archiveSnapshot=archiveRepository.LoadForMigration();
            Check("memory_archive_import_preserves_partition_cursor_and_provenance",archiveRepository.Load().State.Records.Single().Id==activeFact.Id && archiveRepository.LoadArchived().State.Records.Single().Id==archivedTurn.Id && archiveSnapshot.State.ImportedChatEntries==7 && archiveSnapshot.State.Records.Single(r=>r.Id==activeFact.Id).SourceRevisions[archivedTurn.Id]==1);
            Check("memory_archive_import_is_idempotent",archiveImporter.Import(archiveImportPath,false).AlreadyImported && archiveRepository.Load().Revision==1);
            var activeArchiveSnapshot=archiveRepository.Load();
            archiveRepository.Save(activeArchiveSnapshot.State,activeArchiveSnapshot.Revision);
            Check("memory_archive_source_survives_subsequent_active_save",archiveRepository.LoadArchived().State.Records.Single().Id==archivedTurn.Id && archiveRepository.Load().State.Records.Single().Sources.Single()==archivedTurn.Id);
            var rollbackPath=Path.Combine(root,"rollback.zip");
            var rollbackItems=new SqlImportantItemRepository(db,KnowledgeAccessContext.ForSession("archive-import","hutao","desktop",[]));
            rollbackItems.Load();
            var keptItem=new ImportantItem(Guid.NewGuid(),"合成待办交论文",now,now.AddDays(1),now.AddMinutes(-20));
            var forgottenItem=new ImportantItem(Guid.NewGuid(),"已忘记的合成事项",now,null,null);
            rollbackItems.Save([keptItem,forgottenItem]);
            rollbackItems.Save([keptItem]);
            var rollbackManifest=MemoryRollbackPackage.Create(db,KnowledgeAccessContext.ForSession("archive-import","hutao","desktop",[]),rollbackPath);
            var rollbackDirectory=Path.Combine(root,"rollback-review");
            System.IO.Compression.ZipFile.ExtractToDirectory(rollbackPath,rollbackDirectory);
            Check("rollback_payloads_match_manifest",rollbackManifest.FileHashes.All(pair=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(rollbackDirectory,pair.Key))))==pair.Value));
            var rollbackTarget=new SqlConversationMemoryRepository(db,KnowledgeAccessContext.ForSession("rollback-reimport","hutao","desktop",[]));
            new MemoryJsonImporter(rollbackTarget).Import(Path.Combine(rollbackDirectory,"memory.json"),false);
            Check("rollback_json_archive_roundtrip",rollbackManifest.ActiveRecords==1 && rollbackManifest.ArchivedRecords==1 && rollbackTarget.Load().State.Records.Single().Id==activeFact.Id && rollbackTarget.LoadArchived().State.Records.Single().Id==archivedTurn.Id);
            var rollbackSql=new SqliteKnowledgeDatabase(Path.Combine(rollbackDirectory,"knowledge.db"));
            var rollbackSqlMemory=new SqlConversationMemoryRepository(rollbackSql,KnowledgeAccessContext.ForSession("archive-import","hutao","desktop",[])).LoadForMigration();
            Check("rollback_sql_and_json_share_revision",rollbackSqlMemory.Revision==rollbackManifest.StateRevision && rollbackSqlMemory.State.Records.Count==2 && rollbackSqlMemory.ArchivedIds.Count==1);
            var packageBytes=File.ReadAllBytes(rollbackPath);
            var packageOverwrite=false;try{MemoryRollbackPackage.Create(db,KnowledgeAccessContext.ForSession("archive-import","hutao","desktop",[]),rollbackPath);}catch(IOException){packageOverwrite=true;}
            Check("rollback_existing_package_preserved",packageOverwrite && packageBytes.SequenceEqual(File.ReadAllBytes(rollbackPath)));
            var rollbackIdentity=KnowledgeAccessContext.ForSession("archive-import","hutao","desktop",[]);
            var preparedPath=Path.Combine(root,"rollback-prepared");
            var preparedManifest=MemoryRollbackPackage.Prepare(rollbackPath,rollbackIdentity,preparedPath);
            Check("rollback_prepare_validates_and_publishes",preparedManifest.StateRevision==rollbackManifest.StateRevision && File.Exists(Path.Combine(preparedPath,"memory.archive.jsonl")) && !Directory.EnumerateDirectories(root,".rollback-prepare-*").Any());
            var restoredItems=System.Text.Json.JsonSerializer.Deserialize<List<HuTao.Dialogue.Storage.ImportantMemory>>(File.ReadAllBytes(Path.Combine(preparedPath,"important-memories.json")))!;
            Check("rollback_important_items_preserve_legacy_fields",preparedManifest.FormatVersion==2 && preparedManifest.ImportantItems==1 && restoredItems.Single().Id==keptItem.Id && restoredItems.Single().DueAt?.ToUnixTimeMilliseconds()==keptItem.DueAt?.ToUnixTimeMilliseconds() && restoredItems.Single().LastOfferedAt?.ToUnixTimeMilliseconds()==keptItem.LastOfferedAt?.ToUnixTimeMilliseconds());
            var legacyRestoredItems=new HuTao.Dialogue.Storage.ImportantMemoryStore(Path.Combine(preparedPath,"important-memories.json"));
            Check("rollback_important_items_load_in_legacy_without_forgotten_item",legacyRestoredItems.PreparePrompt("还记得论文吗",false,now).Contains("合成待办交论文") && restoredItems.All(i=>i.Id!=forgottenItem.Id));
            MemoryRollbackPackage.Prepare(rollbackPath,rollbackIdentity,Path.Combine(root,"current-prepared"),currentAuthority:db);
            Check("rollback_current_authority_accepts_unchanged_snapshot",Directory.Exists(Path.Combine(root,"current-prepared")));
            var forkIndex=0;
            foreach(var mutation in new[]{
                "UPDATE evidence_units SET text='同版本不同正文' WHERE namespace_id=@n",
                "UPDATE important_items SET due_at=due_at+1000 WHERE namespace_id=@n AND status='active'",
                "UPDATE sources SET locator='different-source' WHERE namespace_id=@n"})
            {
                var forkPath=Path.Combine(root,"rollback-fork-"+(forkIndex++)+".db");
                rollbackSql.Backup(forkPath);var forkDatabase=new SqliteKnowledgeDatabase(forkPath);
                MemoryRollbackPackage.DemandCurrent(rollbackSql,forkDatabase,rollbackIdentity);
                forkDatabase.Write((c,t)=>{using var command=c.CreateCommand();command.Transaction=t;command.CommandText=mutation;command.Parameters.AddWithValue("@n",rollbackIdentity.MemoryNamespace);command.ExecuteNonQuery();return 0;});
                var forkRejected=false;try{MemoryRollbackPackage.DemandCurrent(rollbackSql,forkDatabase,rollbackIdentity);}catch(InvalidOperationException){forkRejected=true;}
                Check("rollback_same_counters_different_content_rejected_"+forkIndex,forkRejected);
            }
            var projectionOnlyPath=Path.Combine(root,"rollback-projection-only.db");
            rollbackSql.Backup(projectionOnlyPath);var projectionOnly=new SqliteKnowledgeDatabase(projectionOnlyPath);
            projectionOnly.Write((c,t)=>{using var command=c.CreateCommand();command.Transaction=t;command.CommandText="UPDATE namespace_state SET graph_checkpoint=graph_checkpoint+1 WHERE namespace_id=@n";command.Parameters.AddWithValue("@n",rollbackIdentity.MemoryNamespace);command.ExecuteNonQuery();return 0;});
            MemoryRollbackPackage.DemandCurrent(rollbackSql,projectionOnly,rollbackIdentity);
            Check("rollback_projection_checkpoint_is_not_memory_divergence",true);
            rollbackItems.Load();rollbackItems.Save([keptItem with {LastOfferedAt=now}]);
            var newerReminderRejected=false;
            try{MemoryRollbackPackage.Prepare(rollbackPath,rollbackIdentity,Path.Combine(root,"stale-reminder"),currentAuthority:db);}catch(InvalidOperationException){newerReminderRejected=true;}
            Check("rollback_current_authority_rejects_newer_reminder_state",newerReminderRejected && !Directory.Exists(Path.Combine(root,"stale-reminder")));
            var beforeForgetPackage=Path.Combine(root,"before-forget.zip");
            MemoryRollbackPackage.Create(db,rollbackIdentity,beforeForgetPackage);
            rollbackItems.Load();rollbackItems.Save([]);
            var deletionRejected=false;
            try{MemoryRollbackPackage.Prepare(beforeForgetPackage,rollbackIdentity,Path.Combine(root,"stale-deletion"),currentAuthority:db);}catch(InvalidOperationException){deletionRejected=true;}
            Check("rollback_current_authority_rejects_forgotten_item_resurrection",deletionRejected && !Directory.Exists(Path.Combine(root,"stale-deletion")));
            var wrongIdentityRejected=false;
            try{MemoryRollbackPackage.Prepare(rollbackPath,importAccess,Path.Combine(root,"wrong-identity"));}catch(InvalidDataException){wrongIdentityRejected=true;}
            Check("rollback_prepare_rejects_wrong_identity",wrongIdentityRejected && !Directory.Exists(Path.Combine(root,"wrong-identity")));
            var boundedRejected=false;
            try{MemoryRollbackPackage.Prepare(rollbackPath,rollbackIdentity,Path.Combine(root,"over-budget"),1);}catch(InvalidDataException){boundedRejected=true;}
            Check("rollback_prepare_enforces_expansion_budget",boundedRejected && !Directory.Exists(Path.Combine(root,"over-budget")));
            var tamperedPackage=Path.Combine(root,"tampered-rollback.zip");
            File.Copy(rollbackPath,tamperedPackage);
            void ReplaceZip(string name,byte[] bytes)
            {
                using var zip=System.IO.Compression.ZipFile.Open(tamperedPackage,System.IO.Compression.ZipArchiveMode.Update);
                zip.GetEntry(name)?.Delete();using var output=zip.CreateEntry(name).Open();output.Write(bytes);
            }
            var alteredJson=System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(rollbackDirectory,"memory.json")).Replace("合成归档提炼","篡改的记忆正文",StringComparison.Ordinal));
            ReplaceZip("memory.json",alteredJson);
            var hashRejected=false;
            try{MemoryRollbackPackage.Prepare(tamperedPackage,rollbackIdentity,Path.Combine(root,"bad-hash"));}catch(InvalidDataException){hashRejected=true;}
            Check("rollback_prepare_rejects_payload_tampering",hashRejected && !Directory.Exists(Path.Combine(root,"bad-hash")));
            var rewrittenHashes=rollbackManifest.FileHashes.ToDictionary(p=>p.Key,p=>p.Value);
            rewrittenHashes["memory.json"]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(alteredJson));
            ReplaceZip("manifest.json",System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rollbackManifest with {FileHashes=rewrittenHashes}));
            var stateMismatch=false;
            try{MemoryRollbackPackage.Prepare(tamperedPackage,rollbackIdentity,Path.Combine(root,"bad-state"));}catch(InvalidDataException){stateMismatch=true;}
            Check("rollback_prepare_checks_sql_json_beyond_hashes",stateMismatch && !Directory.Exists(Path.Combine(root,"bad-state")));
            File.WriteAllBytes(tamperedPackage,packageBytes);
            var alteredImportant=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[]{keptItem with {DueAt=now.AddDays(9)}});
            ReplaceZip("important-memories.json",alteredImportant);
            var importantHashes=rollbackManifest.FileHashes.ToDictionary(p=>p.Key,p=>p.Value);
            importantHashes["important-memories.json"]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(alteredImportant));
            ReplaceZip("manifest.json",System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(rollbackManifest with {FileHashes=importantHashes}));
            var importantMismatch=false;
            try{MemoryRollbackPackage.Prepare(tamperedPackage,rollbackIdentity,Path.Combine(root,"bad-important"));}catch(InvalidDataException){importantMismatch=true;}
            Check("rollback_prepare_checks_reminder_time_against_sql",importantMismatch && !Directory.Exists(Path.Combine(root,"bad-important")));
            ReplaceZip("../escape.json",[1,2,3]);
            var entryRejected=false;
            try{MemoryRollbackPackage.Prepare(tamperedPackage,rollbackIdentity,Path.Combine(root,"bad-entry"));}catch(InvalidDataException){entryRejected=true;}
            Check("rollback_prepare_rejects_unlisted_paths_and_cleans_staging",entryRejected && !File.Exists(Path.Combine(root,"escape.json")) && !Directory.EnumerateDirectories(root,".rollback-prepare-*").Any());
            var changedArchive=archiveRepository.LoadForMigration();
            changedArchive.State.Records.Single(r=>r.Id==archivedTurn.Id).Text="来源已经更新的合成内容";
            archiveRepository.Save(changedArchive.State,changedArchive.Revision);
            var unsafeRollback=Path.Combine(root,"unsafe-rollback.zip");
            var mismatchedSource=false;try{MemoryRollbackPackage.Create(db,KnowledgeAccessContext.ForSession("archive-import","hutao","desktop",[]),unsafeRollback);}catch(InvalidOperationException){mismatchedSource=true;}
            Check("rollback_refuses_old_pin_with_new_source_body",mismatchedSource && !File.Exists(unsafeRollback) && !Directory.EnumerateFiles(root,".rollback-*").Any());
            var invalidArchiveRepository=new SqlConversationMemoryRepository(db,KnowledgeAccessContext.ForSession("archive-invalid","hutao","desktop",[]));
            activeFact.Sources=["missing-archive-source"];
            File.WriteAllText(archiveImportPath,System.Text.Json.JsonSerializer.Serialize(archiveState,MemoryFileState.Json));
            var archiveInvalid=false;try{new MemoryJsonImporter(invalidArchiveRepository).Import(archiveImportPath,false);}catch(InvalidDataException){archiveInvalid=true;}
            Check("memory_archive_invalid_source_leaves_target_empty",archiveInvalid && invalidArchiveRepository.LoadForMigration().State.Records.Count==0 && invalidArchiveRepository.Load().Revision==0);
            activeFact.Sources=[archivedTurn.Id];archivedTurn.Speaker="assistant";
            File.WriteAllText(archiveImportPath,System.Text.Json.JsonSerializer.Serialize(archiveState,MemoryFileState.Json));
            File.WriteAllText(archiveSidecar,System.Text.Json.JsonSerializer.Serialize(archivedTurn,MemoryFileState.Json));
            var archiveAttributionRejected=false;
            try{new MemoryJsonImporter(invalidArchiveRepository).Import(archiveImportPath,false);}catch(InvalidDataException){archiveAttributionRejected=true;}
            Check("memory_archive_transaction_rolls_back_assistant_only_fact",archiveAttributionRejected && invalidArchiveRepository.LoadForMigration().State.Records.Count==0 && invalidArchiveRepository.Load().Revision==0);
            var itemRepository=new SqlImportantItemRepository(db,memoryAccess);
            var items=new HuTao.Dialogue.Storage.ImportantMemoryStore(Path.Combine(root,"unused-items.json"),repository:itemRepository);
            var savedPrompt=items.PreparePrompt("记住明天交论文",false,now);
            var savedItem=itemRepository.Load().Single();
            Check("important_sql_explicit_remember",savedPrompt.Contains("交论文") && savedItem.DueAt is not null && !File.Exists(Path.Combine(root,"unused-items.json")));
            var itemRestart=new HuTao.Dialogue.Storage.ImportantMemoryStore(Path.Combine(root,"unused-items.json"),repository:new SqlImportantItemRepository(db,memoryAccess));
            Check("important_sql_restart_recall",itemRestart.PreparePrompt("还记得论文吗",false,now).Contains("交论文"));
            var beforeEpoch=repository.PrivacyEpochs(memoryAccess)[memoryAccess.MemoryNamespace];
            Check("important_sql_forget",itemRestart.PreparePrompt("忘记论文",false,now).Contains("删除") && itemRepository.Load().Count==0);
            Check("important_forget_epoch_fts",repository.PrivacyEpochs(memoryAccess)[memoryAccess.MemoryNamespace]>beforeEpoch && !repository.Search(memoryAccess,"论文").Any(r=>r.Reference.Id.StartsWith("important:",StringComparison.Ordinal)));
            var job=new MemoryConsolidationRun("test-input-hash","test-fixture","v1");
            var applied=0;
            store.CommitBatch(()=>{applied++;store.MarkConsolidated([turn.Id],now);},run:job);
            store.CommitBatch(()=>applied++,run:job);
            Check("consolidation_job_idempotent",applied==1 && memoryRepository.HasCommitted(job));
            var failedJob=new MemoryConsolidationRun("failed-input","test-fixture","v1");
            try { store.CommitBatch(()=>throw new InvalidDataException("fail"),run:failedJob); } catch(InvalidDataException) { }
            Check("consolidation_job_rollback",!memoryRepository.HasCommitted(failedJob));
            var concurrentStart=store.Count;
            await Task.WhenAll(Enumerable.Range(0,12).Select(i=>Task.Run(()=>store.ObserveTurn("user",$"并发测试消息第{i}条",now.AddMinutes(10+i)))));
            Check("same_store_concurrent_chat_no_loss",store.Count==concurrentStart+12 && memoryRepository.Load().State.Records.Count==store.Count);
            try { db.Write<int>((c,t) => { using var command=c.CreateCommand(); command.Transaction=t; command.CommandText="INSERT INTO namespaces(namespace_id,domain) VALUES('memory:rollback','memory')"; command.ExecuteNonQuery(); throw new InvalidOperationException("injected"); }); }
            catch (InvalidOperationException) { }
            using (var connection=db.Open())
            {
                using var command=connection.CreateCommand(); command.CommandText="SELECT COUNT(*) FROM namespaces WHERE namespace_id='memory:rollback'";
                Check("transaction_failure_rolls_back", (long)command.ExecuteScalar()! == 0);
            }
            var backup=Path.Combine(root,"backup.db"); db.Backup(backup);
            _ = new SqliteKnowledgeDatabase(backup); Check("online_backup_reopens",true);
            using(var held=db.Open())
            using(var transaction=held.BeginTransaction())
            {
                var watch=System.Diagnostics.Stopwatch.StartNew();
                var busy=false;
                try { db.Write((c,t)=>0); }
                catch(SqliteException ex) when(ex.SqliteErrorCode==5) { busy=true; }
                Check("external_write_lock_bounded",busy && watch.Elapsed<TimeSpan.FromSeconds(6));
            }
            using (var connection=db.Open())
            { using var cmd=connection.CreateCommand(); cmd.CommandText="UPDATE schema_migrations SET checksum='tampered'"; cmd.ExecuteNonQuery(); }
            Reject("migration_tamper_rejected", () => new SqliteKnowledgeDatabase(db.FilePath));
            using (var connection=new SqliteConnection($"Data Source={backup};Pooling=False"))
            { connection.Open(); using var cmd=connection.CreateCommand(); cmd.CommandText="INSERT INTO schema_migrations VALUES(99,'future',0)"; cmd.ExecuteNonQuery(); }
            Reject("future_schema_rejected", () => new SqliteKnowledgeDatabase(backup));
            var budget = new RecallBudget(maxQueries:1); Check("shared_query_budget",budget.ReserveQuery() && !budget.ReserveQuery());
            await Task.CompletedTask;
        }
        finally { Directory.Delete(root, recursive:true); }
        return results;
    }
    private sealed class ExportFixtureRepository(MemoryFileState state):IConversationMemoryRepository
    {
        public MemoryRepositorySnapshot Load()=>new(state,1);
        public long Save(MemoryFileState value,long revision,MemoryConsolidationRun? run=null)=>throw new NotSupportedException();
        public bool HasCommitted(MemoryConsolidationRun run)=>false;
    }
}
