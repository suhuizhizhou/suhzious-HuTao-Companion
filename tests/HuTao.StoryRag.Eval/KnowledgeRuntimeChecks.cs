using HuTao.Dialogue.Core;
using HuTao.Dialogue.Runtime;
using HuTao.Foundation.Abstractions;
using HuTao.Knowledge.Memory;
using HuTao.Knowledge.Retrieval;
using HuTao.Knowledge.Storage;
using HuTao.Persona;

internal static class KnowledgeRuntimeChecks
{
    public static async Task<List<CheckResult>> RunAsync()
    {
        var results=new List<CheckResult>();void Check(string name,bool ok)=>results.Add(new("runtime_"+name,ok,"real factory / isolated SQL / fixture LLM"));
        var root=Path.Combine(Path.GetTempPath(),"hutao-runtime-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var oldMemory=Environment.GetEnvironmentVariable("HU_TAO_MEMORY");var oldGate=Environment.GetEnvironmentVariable("HU_TAO_IMMERSION");
        Environment.SetEnvironmentVariable("HU_TAO_MEMORY","true");
        try
        {
            var installSource=Path.Combine(root,"install-source");File.WriteAllText(installSource,"new synthetic payload");
            var installExisting=Path.Combine(root,"install-existing");File.WriteAllText(installExisting,"old synthetic payload");
            var installNew=Path.Combine(root,"install-new");
            var journal=Path.Combine(root,"install-journal");
            RollbackFileJournal.Create(journal,[(installSource,installExisting),(installSource,installNew)]);
            var installTargets=new[]{installExisting,installNew};
            Check("journal_creation_preserves_existing_files",File.ReadAllText(installExisting)=="old synthetic payload" && !File.Exists(installNew));
            // A process may stop after the first atomic replacement, before reaching the second.
            File.Copy(Path.Combine(journal,"0.new"),installExisting,true);
            RollbackFileJournal.Restore(journal,installTargets);
            Check("journal_restores_partial_install",File.ReadAllText(installExisting)=="old synthetic payload" && !File.Exists(installNew));
            RollbackFileJournal.Apply(journal,installTargets);RollbackFileJournal.Apply(journal,installTargets);
            Check("journal_apply_is_restartable",File.ReadAllText(installExisting)=="new synthetic payload" && File.ReadAllText(installNew)=="new synthetic payload");
            File.WriteAllText(installNew,"external change");
            var journalExternalRejected=false;try{RollbackFileJournal.Restore(journal,installTargets);}catch(InvalidOperationException){journalExternalRejected=true;}
            Check("journal_refuses_unrelated_target_changes_before_restore",journalExternalRejected && File.ReadAllText(installExisting)=="new synthetic payload" && File.ReadAllText(installNew)=="external change");
            File.WriteAllText(installNew,"new synthetic payload");RollbackFileJournal.Restore(journal,installTargets);RollbackFileJournal.Restore(journal,installTargets);
            Check("journal_restore_is_restartable",File.ReadAllText(installExisting)=="old synthetic payload" && !File.Exists(installNew));
            var corruptJournal=Path.Combine(root,"corrupt-install-journal");
            var corruptTarget=Path.Combine(root,"corrupt-target.txt");File.WriteAllText(corruptTarget,"corrupt-old");
            RollbackFileJournal.Create(corruptJournal,[(installSource,corruptTarget)]);
            var corruptManifest=Path.Combine(corruptJournal,"manifest.json");var originalManifest=File.ReadAllBytes(corruptManifest);
            File.WriteAllText(corruptManifest,System.Text.Encoding.UTF8.GetString(originalManifest).Replace("\"Version\":1","\"Version\":99"));
            var corruptManifestRejected=false;try{RollbackFileJournal.Apply(corruptJournal,[corruptTarget]);}catch(InvalidDataException){corruptManifestRejected=true;}
            Check("journal_corrupt_manifest_rejected_before_apply",corruptManifestRejected && File.ReadAllText(corruptTarget)=="corrupt-old");
            File.WriteAllBytes(corruptManifest,originalManifest);File.AppendAllText(Path.Combine(corruptJournal,"0.new"),"tampered");
            var corruptPayloadRejected=false;try{RollbackFileJournal.Apply(corruptJournal,[corruptTarget]);}catch(InvalidDataException){corruptPayloadRejected=true;}
            Check("journal_corrupt_payload_rejected_before_apply",corruptPayloadRejected && File.ReadAllText(corruptTarget)=="corrupt-old");
            var legacyLogRoot=Path.Combine(root,"legacy-log-boundary");
            Directory.CreateDirectory(Path.Combine(legacyLogRoot,"persona"));Directory.CreateDirectory(Path.Combine(legacyLogRoot,"data"));
            File.WriteAllText(Path.Combine(legacyLogRoot,"persona","system-prompt.md"),"你是合成测试角色。");
            var legacyLogDefinition=new CharacterDefinition("fixture","测试角色","","","persona","","","missing.json","你好","","忙碌",Tools:["document_reader"]);
            var legacyLogPath=Path.Combine(legacyLogRoot,"data","chat_log_fixture.json");
            var protectedEntries=new[]{new HuTao.Dialogue.Storage.ChatEntry("2026-10-09T10:00:00Z","user","已标记的合成隐私正文",null){Privacy=new("memory:local:fixture:desktop",1)},new HuTao.Dialogue.Storage.ChatEntry("2026-10-09T10:01:00Z","user","混合文件的未标记正文",null)};
            File.WriteAllText(legacyLogPath,System.Text.Json.JsonSerializer.Serialize(protectedEntries));
            var protectedBytes=File.ReadAllBytes(legacyLogPath);
            var legacyLogFactory=new AgentRuntimeFactory(legacyLogRoot,knowledgeOptions:new(KnowledgeRuntimeMode.Legacy,Path.Combine(legacyLogRoot,"unused.db")),llmFactory:_=>new FixtureLlm(false));
            await using(var legacyLogRuntime=legacyLogFactory.Create(legacyLogDefinition))
                Check("legacy_startup_cannot_reimport_sql_stamped_log",legacyLogRuntime.MemoryStore is not null && legacyLogRuntime.MemoryStore.Count==0 && protectedBytes.SequenceEqual(File.ReadAllBytes(legacyLogPath)));
            File.WriteAllText(legacyLogPath,System.Text.Json.JsonSerializer.Serialize(new[]{new HuTao.Dialogue.Storage.ChatEntry("2026-10-09T10:02:00Z","user","正常旧版合成历史",null)}));
            await using(var legacyLogRuntime=legacyLogFactory.Create(legacyLogDefinition))
                Check("legacy_startup_still_imports_unversioned_history",legacyLogRuntime.MemoryStore?.Snapshot().Single().Text=="正常旧版合成历史");
            await using(var explicitDesktop=legacyLogFactory.Create(legacyLogDefinition,memoryScope:"desktop"))
                Check("explicit_desktop_uses_same_memory_and_history",explicitDesktop.MemoryStore?.Snapshot().Single().Text=="正常旧版合成历史" && explicitDesktop.MemoryStore.FilePath==KnowledgeHostPaths.Resolve(legacyLogRoot,legacyLogDefinition,"local").Memory);
            var chatroomPaths=KnowledgeHostPaths.Resolve(legacyLogRoot,legacyLogDefinition,"local","chatroom");
            Check("host_path_mapping_keeps_chatroom_distinct",chatroomPaths.Memory!=KnowledgeHostPaths.Resolve(legacyLogRoot,legacyLogDefinition,"local").Memory && chatroomPaths.ChatLog is not null && chatroomPaths.ChatLog.EndsWith("chat_log_fixture_chatroom.json",StringComparison.Ordinal) && chatroomPaths.Archive.EndsWith("memory_fixture_chatroom.archive.jsonl",StringComparison.Ordinal));
            var oldDesktopPath=Path.Combine(legacyLogRoot,"data","memory_fixture_desktop.json");
            File.WriteAllText(oldDesktopPath,"{}");
            var ambiguousDesktop=false;try{await using var denied=legacyLogFactory.Create(legacyLogDefinition);}catch(InvalidOperationException){ambiguousDesktop=true;}
            Check("old_explicit_desktop_file_requires_reconciliation",ambiguousDesktop && File.ReadAllText(oldDesktopPath)=="{}");
            foreach(var conflictingPath in new[]{"data/memory_fixture.json","data/memory_fixture.archive.jsonl","data/chat_log_fixture.json","data/knowledge/authority-local-fixture-desktop.json.sql-live"})
            {
                var rejectedPath=false;
                try{KnowledgeHostPaths.Resolve(legacyLogRoot,legacyLogDefinition with {ImportantMemoryFile=conflictingPath},"local");}catch(InvalidOperationException){rejectedPath=true;}
                Check("host_paths_reject_important_item_collision_"+Path.GetFileName(conflictingPath),rejectedPath);
            }
            var rollbackDb=new SqliteKnowledgeDatabase(Path.Combine(root,"coordinated-rollback.db"));
            var conflictingAuthority=KnowledgeHostPaths.Resolve(legacyLogRoot,legacyLogDefinition,"local").Authority;
            File.WriteAllText(conflictingAuthority,System.Text.Json.JsonSerializer.Serialize(new {database=rollbackDb.FilePath,space="memory:other:fixture:desktop",lineageId=KnowledgeRecovery.ReadLineage(rollbackDb)}));
            var conflictingAuthorityBytes=File.ReadAllBytes(conflictingAuthority);
            var identityRejected=false;
            try{await using var denied=new AgentRuntimeFactory(legacyLogRoot,knowledgeOptions:new(KnowledgeRuntimeMode.Sql,rollbackDb.FilePath),llmFactory:_=>new FixtureLlm(false)).Create(legacyLogDefinition);}
            catch(InvalidOperationException){identityRejected=true;}
            Check("sql_factory_rejects_foreign_authority_namespace",identityRejected && conflictingAuthorityBytes.SequenceEqual(File.ReadAllBytes(conflictingAuthority)));
            var rollbackAccess=KnowledgeAccessContext.ForSession("coordinator","fixture","desktop",[]);
            var rollbackStore=new ConversationMemoryStore(Path.Combine(root,"unused-rollback.json"),repository:new SqlConversationMemoryRepository(rollbackDb,rollbackAccess));
            rollbackStore.ObserveTurn("user","合成回滚准备记忆",DateTimeOffset.UtcNow);
            var rollbackMarker=Path.Combine(root,"coordinated-authority.json");
            File.WriteAllText(rollbackMarker,System.Text.Json.JsonSerializer.Serialize(new {database=rollbackDb.FilePath,space=rollbackAccess.MemoryNamespace,lineageId=KnowledgeRecovery.ReadLineage(rollbackDb)}));
            var originalMarker=File.ReadAllBytes(rollbackMarker);
            var rollbackPackage=Path.Combine(root,"coordinated-rollback.zip");
            rollbackDb.Write((c,t)=>{using var command=c.CreateCommand();command.Transaction=t;command.CommandText="CREATE TABLE rollback_lock_probe(singleton INTEGER PRIMARY KEY,value INTEGER NOT NULL); INSERT INTO rollback_lock_probe VALUES(1,0)";command.ExecuteNonQuery();return 0;});
            HuTao.Knowledge.Import.MemoryRollbackPackage.Create(rollbackDb,rollbackAccess,rollbackPackage);
            var rollbackFence=new KnowledgeAuthorityFence(rollbackMarker);
            using(var live=rollbackFence.RegisterSqlRuntime())
            {
                var rejected=false;try{KnowledgeRollbackCoordinator.Prepare(rollbackMarker,rollbackDb,rollbackAccess,rollbackPackage,Path.Combine(root,"live-rejected"));}catch(IOException){rejected=true;}
                Check("coordinated_rollback_rejects_live_sql",rejected && !Directory.Exists(Path.Combine(root,"live-rejected")));
            }
            var coordinatedDirectory=Path.Combine(root,"coordinated-prepared");
            KnowledgeRollbackCoordinator.Prepare(rollbackMarker,rollbackDb,rollbackAccess,rollbackPackage,coordinatedDirectory);
            Check("coordinated_rollback_prepares_without_changing_authority",Directory.Exists(coordinatedDirectory) && File.ReadAllBytes(rollbackMarker).SequenceEqual(originalMarker));
            var coordinatedLog=Path.Combine(root,"coordinated-chat.json");
            File.WriteAllText(coordinatedLog,System.Text.Json.JsonSerializer.Serialize(new[]{new HuTao.Dialogue.Storage.ChatEntry("2026-10-09T10:00:00Z","user","保留在日志但不重新生成记忆的合成文字",null){Privacy=new(rollbackAccess.MemoryNamespace,0)}}));
            var withChat=Path.Combine(root,"coordinated-with-chat");
            KnowledgeRollbackCoordinator.Prepare(rollbackMarker,rollbackDb,rollbackAccess,rollbackPackage,withChat,coordinatedLog);
            var convertedLog=new HuTao.Dialogue.Storage.ChatLogStore(Path.Combine(withChat,"chat-log.json")).Load();
            var convertedMemory=new ConversationMemoryStore(Path.Combine(withChat,"legacy-memory.json"));
            var existingCount=convertedMemory.Count;
            var repeatedImport=convertedMemory.ImportChatLog(convertedLog.Select(e=>(e.Time,e.Role,e.Text)),DateTimeOffset.UtcNow);
            Check("coordinated_log_cursor_does_not_reimport_historical_text",convertedLog.Count==1 && repeatedImport==0 && convertedMemory.Count==existingCount);
            var appended=convertedLog.Select(e=>(e.Time,e.Role,e.Text)).Append(("2026-10-09T11:00:00Z","user","回滚之后追加的合成历史"));
            Check("coordinated_log_cursor_allows_new_entries",convertedMemory.ImportChatLog(appended,DateTimeOffset.UtcNow)==1);
            var invalidChatTarget=Path.Combine(root,"coordinated-bad-chat");
            var chatHashRejected=false;try{KnowledgeRollbackCoordinator.Prepare(rollbackMarker,rollbackDb,rollbackAccess,rollbackPackage,invalidChatTarget,coordinatedLog,"invalid-hash");}catch(InvalidDataException){chatHashRejected=true;}
            Check("coordinated_log_failure_does_not_publish_partial_package",chatHashRejected && !Directory.Exists(invalidChatTarget) && !Directory.EnumerateDirectories(root,".host-rollback-*").Any() && File.ReadAllBytes(rollbackMarker).SequenceEqual(originalMarker));
            var installHost=Path.Combine(root,"installation-host");
            Directory.CreateDirectory(Path.Combine(installHost,"persona"));
            File.WriteAllText(Path.Combine(installHost,"persona","system-prompt.md"),"合成安装测试角色");
            var hostInstallPaths=KnowledgeHostPaths.Resolve(installHost,legacyLogDefinition,"coordinator");
            Directory.CreateDirectory(Path.GetDirectoryName(hostInstallPaths.Authority)!);
            File.Copy(rollbackMarker,hostInstallPaths.Authority);
            File.Copy(coordinatedLog,hostInstallPaths.ChatLog!);
            File.WriteAllText(hostInstallPaths.Memory,"{\"old\":true}");
            var originalHostMemory=File.ReadAllBytes(hostInstallPaths.Memory);var originalHostChat=File.ReadAllBytes(hostInstallPaths.ChatLog!);
            var rollbackCommandOutput="";
            async Task<int> RunRollbackCommand(string action,string? prepared=null,string? package=null)
            {
                var arguments=new List<string>{action,"--root",installHost,"--db",rollbackDb.FilePath,"--user","coordinator","--persona",legacyLogDefinition.Id};
                if(prepared is not null){arguments.Add("--prepared");arguments.Add(prepared);}
                if(package is not null){arguments.Add("--package");arguments.Add(package);}
                using var output=new StringWriter();
                var code=await KnowledgeRollbackCommand.RunAsync(arguments.ToArray(),output,id=>id==legacyLogDefinition.Id?legacyLogDefinition:null);
                rollbackCommandOutput=output.ToString();return code;
            }
            var cliRollbackPackage=Path.Combine(root,"cli-rollback-package.zip");
            Check("rollback_command_creates_review_package",await RunRollbackCommand("create",package:cliRollbackPackage)==0 && File.Exists(cliRollbackPackage) && !rollbackCommandOutput.Contains("合成回滚准备记忆"));
            var hostPrepared=Path.Combine(root,"host-install-materials");
            Check("rollback_command_prepares_without_installing",await RunRollbackCommand("prepare",hostPrepared,cliRollbackPackage)==0 && originalHostMemory.SequenceEqual(File.ReadAllBytes(hostInstallPaths.Memory)));
            var preparedLegacyPath=Path.Combine(hostPrepared,"legacy-memory.json");var conversionPath=Path.Combine(hostPrepared,"chat-conversion.json");
            var validLegacyBytes=File.ReadAllBytes(preparedLegacyPath);var validConversionBytes=File.ReadAllBytes(conversionPath);
            using(var heldConversion=new FileStream(conversionPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                var inputLocked=false;try{KnowledgeRollbackInstallation.InstallFiles(hostInstallPaths,rollbackDb,rollbackAccess,hostPrepared);}catch(IOException){inputLocked=true;}
                Check("host_install_locked_material_does_not_touch_targets",inputLocked && originalHostMemory.SequenceEqual(File.ReadAllBytes(hostInstallPaths.Memory)) && !File.Exists(hostInstallPaths.Authority+".rollback-pending.json"));
                using var earlierInput=new FileStream(Path.Combine(hostPrepared,"memory.json"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);
                Check("host_install_partial_lease_failure_releases_inputs",earlierInput.CanWrite);
            }
            var forgedMemory=System.Text.Json.JsonSerializer.Deserialize<MemoryFileState>(validLegacyBytes,MemoryFileState.Json)!;
            forgedMemory.Records[0].Text="与SQL不一致但重新计算了hash的合成正文";
            var forgedBytes=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(forgedMemory,MemoryFileState.Json);
            File.WriteAllBytes(preparedLegacyPath,forgedBytes);
            var forgedConversion=System.Text.Json.JsonSerializer.Deserialize<HostRollbackConversion>(validConversionBytes)! with {LegacyMemorySha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(forgedBytes))};
            File.WriteAllBytes(conversionPath,System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(forgedConversion));
            var forgedInstallRejected=false;try{KnowledgeRollbackInstallation.InstallFiles(hostInstallPaths,rollbackDb,rollbackAccess,hostPrepared);}catch(InvalidDataException){forgedInstallRejected=true;}
            Check("host_install_rechecks_semantics_after_hash_rewrite",forgedInstallRejected && originalHostMemory.SequenceEqual(File.ReadAllBytes(hostInstallPaths.Memory)) && !File.Exists(hostInstallPaths.Authority+".rollback-pending.json") && !Directory.Exists(Path.Combine(hostPrepared,"installation-journal")));
            File.WriteAllBytes(preparedLegacyPath,validLegacyBytes);File.WriteAllBytes(conversionPath,validConversionBytes);

            System.Diagnostics.Process StartRollbackStageProbe(string action,string checkpoint,string signal)
            {
                var executable=Environment.ProcessPath??throw new InvalidOperationException("Probe executable unavailable.");
                var start=new System.Diagnostics.ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true};
                if(string.Equals(Path.GetFileNameWithoutExtension(executable),"dotnet",StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(KnowledgeRuntimeChecks).Assembly.Location);
                foreach(var argument in new[]{"--knowledge-rollback-stage-probe",action,checkpoint,signal,hostInstallPaths.Authority,
                    hostInstallPaths.Memory,hostInstallPaths.Archive,hostInstallPaths.ImportantItems??"-",hostInstallPaths.ChatLog!,rollbackDb.FilePath,
                    "coordinator",legacyLogDefinition.Id,"desktop",hostPrepared})start.ArgumentList.Add(argument);
                return System.Diagnostics.Process.Start(start)!;
            }
            async Task KillAtRollbackStage(string action,string checkpoint,string signal)
            {
                using var child=StartRollbackStageProbe(action,checkpoint,signal);
                try
                {
                    var deadline=DateTimeOffset.UtcNow.AddSeconds(15);
                    while(!File.Exists(signal) && !child.HasExited && DateTimeOffset.UtcNow<deadline)await Task.Delay(20);
                    if(!File.Exists(signal))throw new TimeoutException($"Rollback probe did not reach {checkpoint}; exit={(child.HasExited?child.ExitCode:null)}");
                    child.Kill(entireProcessTree:true);await child.WaitForExitAsync();
                }
                finally{if(!child.HasExited){child.Kill(entireProcessTree:true);await child.WaitForExitAsync();}}
            }
            void RemoveRecoveredJournal()
            {
                var recoveredJournal=Path.Combine(hostPrepared,"installation-journal");
                if(Directory.Exists(recoveredJournal))Directory.Delete(recoveredJournal,true);
            }
            File.WriteAllText(hostInstallPaths.Authority,"{corrupt-authority");
            var corruptAuthorityRejected=false;
            try{KnowledgeRollbackInstallation.InstallFiles(hostInstallPaths,rollbackDb,rollbackAccess,hostPrepared);}
            catch(System.Text.Json.JsonException){corruptAuthorityRejected=true;}
            File.WriteAllBytes(hostInstallPaths.Authority,originalMarker);
            Check("corrupt_authority_rejected_before_pending_or_file_apply",corruptAuthorityRejected &&
                !File.Exists(hostInstallPaths.Authority+".rollback-pending.json") && originalHostMemory.SequenceEqual(File.ReadAllBytes(hostInstallPaths.Memory)));
            var pendingStopSignal=Path.Combine(root,"rollback-pending-stop.ready");
            await KillAtRollbackStage("install","pending-written",pendingStopSignal);
            var pendingStopStartupBlocked=false;
            try{await using var denied=new AgentRuntimeFactory(installHost,knowledgeOptions:new(KnowledgeRuntimeMode.Sql,rollbackDb.FilePath,UserId:"coordinator"),llmFactory:_=>new FixtureLlm(false)).Create(legacyLogDefinition);}
            catch(InvalidOperationException){pendingStopStartupBlocked=true;}
            Check("hard_stop_after_pending_keeps_old_files_and_blocks_activation",pendingStopStartupBlocked && File.Exists(hostInstallPaths.Authority+".rollback-pending.json") &&
                originalHostMemory.SequenceEqual(File.ReadAllBytes(hostInstallPaths.Memory)) && originalHostChat.SequenceEqual(File.ReadAllBytes(hostInstallPaths.ChatLog!)));
            var pendingPath=hostInstallPaths.Authority+".rollback-pending.json";var validPendingBytes=File.ReadAllBytes(pendingPath);
            File.WriteAllText(pendingPath,"{corrupt-pending");
            var corruptPendingRejected=false;try{KnowledgeRollbackInstallation.RestoreFiles(hostInstallPaths,rollbackDb,rollbackAccess,hostPrepared);}
            catch(System.Text.Json.JsonException){corruptPendingRejected=true;}
            Check("corrupt_pending_rejected_without_changing_authority_or_targets",corruptPendingRejected && File.Exists(hostInstallPaths.Authority) &&
                originalHostMemory.SequenceEqual(File.ReadAllBytes(hostInstallPaths.Memory)));
            File.WriteAllBytes(pendingPath,validPendingBytes);
            KnowledgeRollbackInstallation.RestoreFiles(hostInstallPaths,rollbackDb,rollbackAccess,hostPrepared);RemoveRecoveredJournal();
            Check("hard_stop_after_pending_restores_clean_sql_state",!File.Exists(hostInstallPaths.Authority+".rollback-pending.json") && File.Exists(hostInstallPaths.Authority));

            var applyStopSignal=Path.Combine(root,"rollback-file-stop.ready");
            await KillAtRollbackStage("install","file-replaced:0",applyStopSignal);
            var applyStopStartupBlocked=false;
            try{await using var denied=new AgentRuntimeFactory(installHost,knowledgeOptions:new(KnowledgeRuntimeMode.Legacy,rollbackDb.FilePath,UserId:"coordinator"),llmFactory:_=>new FixtureLlm(false)).Create(legacyLogDefinition);}
            catch(InvalidOperationException){applyStopStartupBlocked=true;}
            Check("hard_stop_during_file_apply_leaves_recoverable_mixed_state",applyStopStartupBlocked && File.Exists(hostInstallPaths.Authority+".rollback-pending.json") &&
                !originalHostMemory.SequenceEqual(File.ReadAllBytes(hostInstallPaths.Memory)) && originalHostChat.SequenceEqual(File.ReadAllBytes(hostInstallPaths.ChatLog!)));
            KnowledgeRollbackInstallation.RestoreFiles(hostInstallPaths,rollbackDb,rollbackAccess,hostPrepared);RemoveRecoveredJournal();
            Check("hard_stop_during_file_apply_restores_all_originals",originalHostMemory.SequenceEqual(File.ReadAllBytes(hostInstallPaths.Memory)) &&
                originalHostChat.SequenceEqual(File.ReadAllBytes(hostInstallPaths.ChatLog!)) && !File.Exists(hostInstallPaths.Authority+".rollback-pending.json"));

            KnowledgeRollbackInstallation.InstallFiles(hostInstallPaths,rollbackDb,rollbackAccess,hostPrepared);
            var authorityStopSignal=Path.Combine(root,"rollback-authority-stop.ready");
            await KillAtRollbackStage("complete","authority-renamed",authorityStopSignal);
            var authorityStopStartupBlocked=false;
            try{await using var denied=new AgentRuntimeFactory(installHost,knowledgeOptions:new(KnowledgeRuntimeMode.Legacy,rollbackDb.FilePath,UserId:"coordinator"),llmFactory:_=>new FixtureLlm(false)).Create(legacyLogDefinition);}
            catch(InvalidOperationException){authorityStopStartupBlocked=true;}
            Check("hard_stop_after_authority_rename_keeps_pending_fence",authorityStopStartupBlocked && !File.Exists(hostInstallPaths.Authority) &&
                File.Exists(hostInstallPaths.Authority+".retired-sql.json") && File.Exists(hostInstallPaths.Authority+".legacy-authority.json") &&
                File.Exists(hostInstallPaths.Authority+".rollback-pending.json"));
            var retiredPath=hostInstallPaths.Authority+".retired-sql.json";var validRetiredBytes=File.ReadAllBytes(retiredPath);
            File.WriteAllText(retiredPath,"{corrupt-retired-authority");
            var corruptRetiredRejected=false;try{KnowledgeRollbackInstallation.RestoreFiles(hostInstallPaths,rollbackDb,rollbackAccess,hostPrepared);}
            catch(System.Text.Json.JsonException){corruptRetiredRejected=true;}
            Check("corrupt_retired_authority_rejected_with_pending_fence_intact",corruptRetiredRejected &&
                File.Exists(hostInstallPaths.Authority+".rollback-pending.json") && File.Exists(hostInstallPaths.Authority+".legacy-authority.json"));
            File.WriteAllBytes(retiredPath,validRetiredBytes);
            KnowledgeRollbackInstallation.RestoreFiles(hostInstallPaths,rollbackDb,rollbackAccess,hostPrepared);RemoveRecoveredJournal();
            Check("hard_stop_after_authority_rename_restores_sql_authority",File.Exists(hostInstallPaths.Authority) &&
                !File.Exists(hostInstallPaths.Authority+".legacy-authority.json") && !File.Exists(hostInstallPaths.Authority+".rollback-pending.json") &&
                originalHostMemory.SequenceEqual(File.ReadAllBytes(hostInstallPaths.Memory)) && originalHostChat.SequenceEqual(File.ReadAllBytes(hostInstallPaths.ChatLog!)));

            KnowledgeRollbackInstallation.InstallFiles(hostInstallPaths,rollbackDb,rollbackAccess,hostPrepared);
            Check("host_install_files_leave_activation_pending",File.Exists(hostInstallPaths.Authority+".rollback-pending.json") && new ConversationMemoryStore(hostInstallPaths.Memory).Count==1 && File.ReadAllBytes(hostInstallPaths.Authority).SequenceEqual(originalMarker));
            var pendingFactoryRejected=false;
            try{await using var denied=new AgentRuntimeFactory(installHost,knowledgeOptions:new(KnowledgeRuntimeMode.Sql,rollbackDb.FilePath,UserId:"coordinator"),llmFactory:_=>new FixtureLlm(false)).Create(legacyLogDefinition);}
            catch(InvalidOperationException){pendingFactoryRejected=true;}
            Check("host_pending_install_blocks_sql_startup",pendingFactoryRejected);
            Check("rollback_command_restores_pending_installation",await RunRollbackCommand("restore",hostPrepared)==0);
            Check("host_restore_recovers_original_files_and_unblocks",!File.Exists(hostInstallPaths.Authority+".rollback-pending.json") && originalHostMemory.SequenceEqual(File.ReadAllBytes(hostInstallPaths.Memory)) && originalHostChat.SequenceEqual(File.ReadAllBytes(hostInstallPaths.ChatLog!)) && !File.Exists(hostInstallPaths.Archive));
            var publishFailurePrepared=Path.Combine(root,"host-publish-failure-materials");
            KnowledgeRollbackCoordinator.Prepare(hostInstallPaths.Authority,rollbackDb,rollbackAccess,rollbackPackage,publishFailurePrepared,hostInstallPaths.ChatLog);
            KnowledgeRollbackInstallation.InstallFiles(hostInstallPaths,rollbackDb,rollbackAccess,publishFailurePrepared);
            var blockedLegacyPath=hostInstallPaths.Authority+".legacy-authority.json";
            Directory.CreateDirectory(blockedLegacyPath);
            var publicationFailed=false;
            try{KnowledgeRollbackInstallation.Complete(hostInstallPaths,rollbackDb,rollbackAccess,publishFailurePrepared);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){publicationFailed=true;}
            finally{Directory.Delete(blockedLegacyPath);}
            Check("legacy_publication_failure_retains_pending_and_sql_marker",publicationFailed && File.Exists(hostInstallPaths.Authority) &&
                File.Exists(hostInstallPaths.Authority+".rollback-pending.json") && !File.Exists(blockedLegacyPath));
            var probeExecutable=Environment.ProcessPath??throw new InvalidOperationException("Probe executable unavailable.");
            var retiredProbeStart=new System.Diagnostics.ProcessStartInfo(probeExecutable){UseShellExecute=false,CreateNoWindow=true};
            if(string.Equals(Path.GetFileNameWithoutExtension(probeExecutable),"dotnet",StringComparison.OrdinalIgnoreCase))retiredProbeStart.ArgumentList.Add(typeof(KnowledgeRuntimeChecks).Assembly.Location);
            foreach(var argument in new[]{"--knowledge-lock-process-probe","retired-write",rollbackDb.FilePath,Path.Combine(root,"retired-probe"),rollbackAccess.MemoryNamespace})retiredProbeStart.ArgumentList.Add(argument);
            using(var retiredWriter=System.Diagnostics.Process.Start(retiredProbeStart)!)
            {
                try
                {
                    await retiredWriter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Check("publication_failure_keeps_separate_process_writer_retired",retiredWriter.ExitCode==19);
                }
                finally{if(!retiredWriter.HasExited){retiredWriter.Kill(entireProcessTree:true);await retiredWriter.WaitForExitAsync();}}
            }
            KnowledgeRollbackInstallation.RestoreFiles(hostInstallPaths,rollbackDb,rollbackAccess,publishFailurePrepared);
            Check("publication_failure_restores_original_host_files",originalHostMemory.SequenceEqual(File.ReadAllBytes(hostInstallPaths.Memory)) &&
                originalHostChat.SequenceEqual(File.ReadAllBytes(hostInstallPaths.ChatLog!)) && !File.Exists(hostInstallPaths.Authority+".rollback-pending.json"));
            var finalPrepared=Path.Combine(root,"host-final-materials");
            KnowledgeRollbackCoordinator.Prepare(hostInstallPaths.Authority,rollbackDb,rollbackAccess,rollbackPackage,finalPrepared,hostInstallPaths.ChatLog);
            Check("rollback_command_installs_reviewed_files",await RunRollbackCommand("install",finalPrepared)==0);
            var pendingBeforeHandoff=File.ReadAllBytes(hostInstallPaths.Authority+".rollback-pending.json");
            using var oldSqlConnection=rollbackDb.Open();
            Check("rollback_command_completes_authority_handoff",await RunRollbackCommand("complete",finalPrepared)==0);
            var oldWriterRejected=false;
            using(var command=oldSqlConnection.CreateCommand())
            {
                command.CommandText="UPDATE namespace_state SET commit_seq=commit_seq+1 WHERE namespace_id=@n";
                command.Parameters.AddWithValue("@n",rollbackAccess.MemoryNamespace);
                try{command.ExecuteNonQuery();}catch(Microsoft.Data.Sqlite.SqliteException ex) when(ex.SqliteErrorCode==19){oldWriterRejected=true;}
            }
            Check("retirement_blocks_connection_opened_before_handoff",oldWriterRejected);
            var repositoryWriterRejected=false;
            try{new SqlEvidenceRepository(rollbackDb).Put(rollbackAccess.MemoryNamespace,new("late-write","迟到写入","user","late-write","turn"));}
            catch(Microsoft.Data.Sqlite.SqliteException ex) when(ex.SqliteErrorCode==19){repositoryWriterRejected=true;}
            Check("retirement_blocks_stale_repository_writer",repositoryWriterRejected);
            var unrelatedSpace=KnowledgeAccessContext.ForSession("unrelated-handoff","fixture","desktop",[]).MemoryNamespace;
            var unrelatedWrite=new SqlEvidenceRepository(rollbackDb).Put(unrelatedSpace,new("other-write","其他会话","user","other-write","turn"));
            Check("retirement_does_not_block_other_namespace",unrelatedWrite.Namespace==unrelatedSpace);
            using(var command=oldSqlConnection.CreateCommand())
            {
                command.Parameters.AddWithValue("@n",rollbackAccess.MemoryNamespace);command.Parameters.AddWithValue("@other",unrelatedSpace);
                var blockedMutations=0;
                foreach(var mutation in new[]{"DELETE FROM evidence_units WHERE namespace_id=@n","UPDATE sources SET namespace_id=@other WHERE namespace_id=@n","UPDATE sources SET namespace_id=@n WHERE namespace_id=@other"})
                {
                    command.CommandText=mutation;
                    try{command.ExecuteNonQuery();}
                    catch(Microsoft.Data.Sqlite.SqliteException ex) when(ex.SqliteErrorCode==19 && ex.Message.Contains("Namespace SQL authority retired")){blockedMutations++;}
                }
                Check("retirement_blocks_delete_and_scope_moves",blockedMutations==3);
            }
            Check("host_handoff_records_legacy_and_retires_sql_authority",!File.Exists(hostInstallPaths.Authority) && File.Exists(hostInstallPaths.Authority+".retired-sql.json") && File.Exists(hostInstallPaths.Authority+".legacy-authority.json") && !File.Exists(hostInstallPaths.Authority+".rollback-pending.json"));
            // Reproduce interruption after authority rename but before final pending-marker removal.
            File.WriteAllBytes(hostInstallPaths.Authority+".rollback-pending.json",pendingBeforeHandoff);
            var legacyDuringHandoff=false;
            try{await using var denied=new AgentRuntimeFactory(installHost,knowledgeOptions:new(KnowledgeRuntimeMode.Legacy,rollbackDb.FilePath,UserId:"coordinator"),llmFactory:_=>new FixtureLlm(false)).Create(legacyLogDefinition);}
            catch(InvalidOperationException){legacyDuringHandoff=true;}
            Check("interrupted_handoff_keeps_legacy_startup_blocked",legacyDuringHandoff);
            KnowledgeRollbackInstallation.Complete(hostInstallPaths,rollbackDb,rollbackAccess,finalPrepared);
            await using(var finalLegacy=new AgentRuntimeFactory(installHost,knowledgeOptions:new(KnowledgeRuntimeMode.Legacy,rollbackDb.FilePath,UserId:"coordinator"),llmFactory:_=>new FixtureLlm(false)).Create(legacyLogDefinition))
                Check("completed_handoff_starts_legacy_with_sql_snapshot",finalLegacy.Knowledge is null && finalLegacy.MemoryStore?.Count==1 && finalLegacy.MemoryStore.Snapshot().Single().Text=="合成回滚准备记忆");
            var retiredSqlRejected=false;
            try{await using var denied=new AgentRuntimeFactory(installHost,knowledgeOptions:new(KnowledgeRuntimeMode.Sql,rollbackDb.FilePath,UserId:"coordinator"),llmFactory:_=>new FixtureLlm(false)).Create(legacyLogDefinition);}
            catch(InvalidOperationException){retiredSqlRejected=true;}
            Check("retired_sql_cannot_reclaim_legacy_conversation",retiredSqlRejected && !File.Exists(hostInstallPaths.Authority));
            // Simulate interrupted completion; recovery restores SQL only after legacy has stopped.
            File.WriteAllBytes(hostInstallPaths.Authority+".rollback-pending.json",pendingBeforeHandoff);
            KnowledgeRollbackInstallation.RestoreFiles(hostInstallPaths,rollbackDb,rollbackAccess,finalPrepared);
            using(var command=oldSqlConnection.CreateCommand())
            {
                command.CommandText="UPDATE namespace_state SET commit_seq=commit_seq WHERE namespace_id=@n";
                command.Parameters.AddWithValue("@n",rollbackAccess.MemoryNamespace);
                Check("interrupted_handoff_restore_reopens_sql_after_files",command.ExecuteNonQuery()==1 && File.Exists(hostInstallPaths.Authority) && !File.Exists(hostInstallPaths.Authority+".rollback-pending.json"));
            }
            var probeSignal=Path.Combine(root,"writer-probe");
            System.Diagnostics.Process StartProbe(string mode)
            {
                var executable=Environment.ProcessPath??throw new InvalidOperationException("Probe executable path unavailable.");
                var start=new System.Diagnostics.ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true};
                if(string.Equals(Path.GetFileNameWithoutExtension(executable),"dotnet",StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(KnowledgeRuntimeChecks).Assembly.Location);
                foreach(var argument in new[]{"--knowledge-lock-process-probe",mode,rollbackDb.FilePath,probeSignal})start.ArgumentList.Add(argument);
                return System.Diagnostics.Process.Start(start)!;
            }
            using(var writerLock=rollbackDb.Open())
            using(var stopped=writerLock.BeginTransaction(deferred:false))
            using(var blockedWriter=StartProbe("write"))
            {
                try
                {
                    await blockedWriter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Check("sqlite_stopped_writes_blocks_separate_process",blockedWriter.ExitCode==5);
                }
                finally{if(!blockedWriter.HasExited){blockedWriter.Kill(entireProcessTree:true);await blockedWriter.WaitForExitAsync();}}
            }
            using(var holdingWriter=StartProbe("hold"))
            {
                try
                {
                    var readyTimer=System.Diagnostics.Stopwatch.StartNew();
                    while(!File.Exists(probeSignal+".ready") && !holdingWriter.HasExited && readyTimer.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(20);
                    if(!File.Exists(probeSignal+".ready"))throw new InvalidOperationException("Writer probe failed to acquire its transaction.");
                    var conflictRejected=false;
                    try{KnowledgeRollbackCoordinator.Prepare(rollbackMarker,rollbackDb,rollbackAccess,rollbackPackage,Path.Combine(root,"writer-conflict"));}
                    catch(Microsoft.Data.Sqlite.SqliteException ex) when(ex.SqliteErrorCode is 5 or 6){conflictRejected=true;}
                    Check("coordinator_rejects_separate_process_write_transaction",conflictRejected && !Directory.Exists(Path.Combine(root,"writer-conflict")));
                    File.WriteAllText(probeSignal+".release","release");
                    await holdingWriter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check("separate_writer_finishes_after_release",holdingWriter.ExitCode==0);
                }
                finally{if(!holdingWriter.HasExited){holdingWriter.Kill(entireProcessTree:true);await holdingWriter.WaitForExitAsync();}}
            }
            // A hard stop of the probe must release only its own SQLite handle via the OS.
            // The next probe is a fresh child process; no process-name based cleanup is used.
            File.Delete(probeSignal+".ready");File.Delete(probeSignal+".release");
            long ProbeValue()
            {
                using var connection=rollbackDb.Open();
                using var command=connection.CreateCommand();
                command.CommandText="SELECT value FROM rollback_lock_probe WHERE singleton=1";
                return Convert.ToInt64(command.ExecuteScalar());
            }
            var committedBeforeKill=ProbeValue();
            using var killedProbe=StartProbe("hold");
            try
            {
                var killTimer=System.Diagnostics.Stopwatch.StartNew();
                while(!File.Exists(probeSignal+".ready") && !killedProbe.HasExited && killTimer.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(20);
                if(!File.Exists(probeSignal+".ready"))throw new InvalidOperationException("Killed writer probe failed to acquire its transaction.");
                killedProbe.Kill(entireProcessTree:true);
                await killedProbe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check("hard_stopped_child_rolls_back_uncommitted_write",ProbeValue()==committedBeforeKill);
                using var recoveredWriter=StartProbe("write");
                try
                {
                    await recoveredWriter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check("hard_stopped_child_releases_sqlite_lock",recoveredWriter.ExitCode==0);
                    Check("recovered_child_commits_exactly_once",ProbeValue()==committedBeforeKill+1);
                }
                finally{if(!recoveredWriter.HasExited){recoveredWriter.Kill(entireProcessTree:true);await recoveredWriter.WaitForExitAsync();}}
            }
            finally{if(!killedProbe.HasExited){killedProbe.Kill(entireProcessTree:true);await killedProbe.WaitForExitAsync();}}
            KnowledgeRollbackCoordinator.Prepare(rollbackMarker,rollbackDb,rollbackAccess,rollbackPackage,Path.Combine(root,"after-writer-release"));
            Check("coordinator_recovers_after_external_writer_exits",Directory.Exists(Path.Combine(root,"after-writer-release")));
            rollbackStore.ObserveTurn("user","准备之后新增的合成记忆",DateTimeOffset.UtcNow);
            var staleRollback=false;try{KnowledgeRollbackCoordinator.Prepare(rollbackMarker,rollbackDb,rollbackAccess,rollbackPackage,Path.Combine(root,"stale-coordinated"));}catch(InvalidOperationException){staleRollback=true;}
            Check("coordinated_rollback_rejects_stale_package_and_keeps_authority",staleRollback && !Directory.Exists(Path.Combine(root,"stale-coordinated")) && File.ReadAllBytes(rollbackMarker).SequenceEqual(originalMarker));
            using(var afterFailure=rollbackFence.RegisterSqlRuntime())
                Check("coordinated_rollback_failure_releases_exclusive_handles",true);
            var cleanup=new PendingRuntimeCleanup();
            var failingOwner=new CleanupFixture();
            var cleanupFailed=false;
            try {await cleanup.RetireAsync(failingOwner);}catch(IOException){cleanupFailed=true;}
            Check("candidate_cleanup_reports_failure",cleanupFailed && failingOwner.Calls==1);
            failingOwner.Fail=false;
            await cleanup.RetryAsync();await cleanup.RetryAsync();
            Check("candidate_cleanup_retains_and_retries_once",failingOwner.Calls==2);
            var firstOwner=new CleanupFixture();var secondOwner=new CleanupFixture();
            foreach(var owner in new[]{firstOwner,secondOwner})
                try {await cleanup.RetireAsync(owner);}catch(IOException){}
            secondOwner.Fail=false;
            var aggregated=false;
            try {await cleanup.RetryAsync();}catch(AggregateException ex){aggregated=ex.InnerExceptions.Count==1;}
            Check("cleanup_failure_does_not_skip_other_owners",aggregated && firstOwner.Calls==2 && secondOwner.Calls==2);
            firstOwner.Fail=false;await cleanup.RetryAsync();
            Check("cleanup_retry_keeps_only_failed_owner",firstOwner.Calls==3 && secondOwner.Calls==2);
            {
                var archiveDb=new SqliteKnowledgeDatabase(Path.Combine(root,"archive.db"));
                var archiveAccess=KnowledgeAccessContext.ForSession("archive","fixture","desktop",[]);
                var archiveRepository=new SqlConversationMemoryRepository(archiveDb,archiveAccess,100);
                var archiveStore=new ConversationMemoryStore(Path.Combine(root,"archive-unused.json"),new MemoryOptions{MaxActiveRecords=100},repository:archiveRepository);
                MemoryRecord? anchor=null;
                archiveStore.CommitBatch(()=>
                {
                    anchor=archiveStore.ObserveTurn("user","独特归档锚点",DateTimeOffset.Now.AddDays(-10),deduplicate:false);
                    for(var i=0;i<104;i++)archiveStore.ObserveTurn("user","合成普通轮次"+i,DateTimeOffset.Now.AddMinutes(-i),deduplicate:false);
                });
                var archived=archiveRepository.LoadArchived();
                var exportRejected=false;
                try{new HuTao.Knowledge.Import.MemoryJsonImporter(archiveRepository).Export(Path.Combine(root,"incomplete-export.json"));}catch(InvalidOperationException){exportRejected=true;}
                Check("sql_archive_not_silently_lost_in_legacy_export",exportRejected && !File.Exists(Path.Combine(root,"incomplete-export.json")));
                Check("sql_archive_caps_active_without_deleting_sources",archiveStore.Count==100 && archived.State.Records.Count==5 && archived.State.Records.Any(r=>r.Id==anchor!.Id) && new SqlEvidenceRepository(archiveDb).Read(archiveAccess,new(archiveAccess.MemoryNamespace,anchor!.Id,1))?.Text=="独特归档锚点");
                archiveStore.ObserveTurn("user","下一条合成对话",DateTimeOffset.Now,deduplicate:false);
                Check("sql_archive_survives_save_and_restart",new SqlConversationMemoryRepository(archiveDb,archiveAccess,100).Load().State.Records.Count==100 && archiveRepository.LoadArchived().State.Records.Count==6);
                new KnowledgeDeletionService(archiveDb).Forget(archiveAccess,archiveAccess.MemoryNamespace,[anchor!.Id]);
                Check("sql_archived_memory_can_be_forgotten",archiveRepository.LoadArchived().State.Records.All(r=>r.Id!=anchor.Id) && new SqlEvidenceRepository(archiveDb).Read(archiveAccess,new(archiveAccess.MemoryNamespace,anchor.Id,1)) is null);
                var package=Path.Combine(root,"archive-backup.zip");var manifest=KnowledgeBackupPackage.Create(archiveDb,package);
                var restoredPath=Path.Combine(root,"archive-restored.db");
                var restoredManifest=KnowledgeBackupPackage.Restore(package,restoredPath);
                var restoredDb=new SqliteKnowledgeDatabase(restoredPath);
                Check("backup_preserves_database_lineage",KnowledgeRecovery.ReadLineage(archiveDb)==KnowledgeRecovery.ReadLineage(restoredDb));
                var identicalDiff=KnowledgeRecoveryDiff.Compare(restoredPath,archiveDb.FilePath);
                Check("recovery_diff_identical_snapshot_never_authorizes_activation",identicalDiff.Identical && !identicalDiff.ActivationAuthorized);
                var branchAPath=Path.Combine(root,"branch-a.db");var branchBPath=Path.Combine(root,"branch-b.db");
                archiveDb.Backup(branchAPath);archiveDb.Backup(branchBPath);
                var branchA=new SqliteKnowledgeDatabase(branchAPath);var branchB=new SqliteKnowledgeDatabase(branchBPath);
                new SqlEvidenceRepository(branchA).Put(archiveAccess.MemoryNamespace,new("same-revision","分支甲的合成秘密","user","branch-source","turn"));
                new SqlEvidenceRepository(branchB).Put(archiveAccess.MemoryNamespace,new("same-revision","分支乙的合成秘密","user","branch-source","turn"));
                new SqlEvidenceRepository(branchA).Put(archiveAccess.MemoryNamespace,new("only-a","仅甲存在","user","only-a","turn"));
                new SqlEvidenceRepository(branchB).Put(archiveAccess.MemoryNamespace,new("only-b","仅乙存在","user","only-b","turn"));
                var branchDiff=KnowledgeRecoveryDiff.Compare(branchAPath,branchBPath);
                var evidenceDiff=branchDiff.Tables.Single(t=>t.Table=="evidence_units");
                Check("recovery_diff_detects_same_revision_divergence_and_one_sided_rows",!branchDiff.Identical && evidenceDiff.ChangedSameKey==1 && evidenceDiff.BaselineOnly==1 && evidenceDiff.CurrentOnly==1);
                Check("recovery_diff_report_does_not_contain_private_bodies",!System.Text.Json.JsonSerializer.Serialize(branchDiff).Contains("合成秘密"));
                var sharedRecords=archiveRepository.Load().State.Records.Take(2).ToArray();
                new KnowledgeDeletionService(branchA).Forget(archiveAccess,archiveAccess.MemoryNamespace,[sharedRecords[0].Id]);
                new KnowledgeDeletionService(branchB).Forget(archiveAccess,archiveAccess.MemoryNamespace,[sharedRecords[1].Id]);
                var adoptionStore=new ConversationMemoryStore(Path.Combine(root,"branch-unused.json"),repository:new SqlConversationMemoryRepository(branchA,archiveAccess));
                var adoptionOriginal=adoptionStore.ObserveTurn("user","待审阅采纳的独有原始对话。",DateTimeOffset.Now)!;
                var provenanceRepository=new SqlConversationMemoryRepository(branchA,archiveAccess);
                var provenanceState=provenanceRepository.Load();
                var adoptionAnswer=new MemoryRecord{Id="recovery-answer",Kind=MemoryKind.Turn,Speaker="assistant",Text="这是对待采纳原文的合成回复。",ObservedAt=DateTimeOffset.Now,RecordedAt=DateTimeOffset.Now,Sources=[adoptionOriginal.Id],SourceRevisions=new(){{adoptionOriginal.Id,1}}};
                var divergentAnswer=new MemoryRecord{Id="divergent-answer",Kind=MemoryKind.Turn,Speaker="assistant",Text="这是对分支甲的合成回复。",ObservedAt=DateTimeOffset.Now,RecordedAt=DateTimeOffset.Now,Sources=["same-revision"],SourceRevisions=new(){{"same-revision",1}}};
                provenanceState.State.Records.Add(adoptionAnswer);provenanceState.State.Records.Add(divergentAnswer);
                provenanceRepository.Save(provenanceState.State,provenanceState.Revision);
                var factStore=new ConversationMemoryStore(Path.Combine(root,"fact-unused.json"),repository:new SqlConversationMemoryRepository(branchA,archiveAccess));
                var factOriginal=factStore.ObserveTurn("user","我不喜欢咖啡。",DateTimeOffset.Now)!;
                var branchFacts=new HuTao.Knowledge.Facts.SqlAssertionRepository(branchA);
                branchFacts.RegisterEntity(archiveAccess,archiveAccess.MemoryNamespace,"user","person","用户");
                new HuTao.Knowledge.Facts.SqlAssertionRepository(branchB).RegisterEntity(archiveAccess,archiveAccess.MemoryNamespace,"user","person","用户");
                var recoveryValidFrom=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds());
                var recoveryValidTo=recoveryValidFrom.AddDays(2);
                branchFacts.Put(archiveAccess,archiveAccess.MemoryNamespace,new("recovery-negative","user","prefers",null,"咖啡",-1,new Dictionary<string,string>(),[new(new(archiveAccess.MemoryNamespace,factOriginal.Id,1),0,factOriginal.Text.Length,factOriginal.Text)],ValidFrom:recoveryValidFrom,ValidTo:recoveryValidTo),DateTimeOffset.UtcNow);
                var catOriginal=factStore.ObserveTurn("user","我照顾一只叫大咪的猫。",DateTimeOffset.Now)!;
                branchFacts.RegisterEntity(archiveAccess,archiveAccess.MemoryNamespace,"recovery-cat","animal","大咪");
                branchFacts.Put(archiveAccess,archiveAccess.MemoryNamespace,new("recovery-care","user","cares_for","recovery-cat",null,1,new Dictionary<string,string>(),[new(new(archiveAccess.MemoryNamespace,catOriginal.Id,1),0,catOriginal.Text.Length,catOriginal.Text)]),DateTimeOffset.UtcNow);
                branchFacts.RegisterEntity(archiveAccess,archiveAccess.MemoryNamespace,"ungrounded-cat","animal","无出处名字");
                branchFacts.Put(archiveAccess,archiveAccess.MemoryNamespace,new("ungrounded-care","user","cares_for","ungrounded-cat",null,1,new Dictionary<string,string>(),[new(new(archiveAccess.MemoryNamespace,catOriginal.Id,1),0,catOriginal.Text.Length,catOriginal.Text)]),DateTimeOffset.UtcNow);
                using(var c=branchA.Open())using(var command=c.CreateCommand())
                {command.CommandText="INSERT INTO mentions VALUES(@n,@e,1,'recovery-cat',@start,@end)";command.Parameters.AddWithValue("@n",archiveAccess.MemoryNamespace);command.Parameters.AddWithValue("@e",catOriginal.Id);command.Parameters.AddWithValue("@start",catOriginal.Text.IndexOf("大咪",StringComparison.Ordinal));command.Parameters.AddWithValue("@end",catOriginal.Text.IndexOf("大咪",StringComparison.Ordinal)+2);command.ExecuteNonQuery();}
                var branchPackage=Path.Combine(root,"branch-a.zip");KnowledgeBackupPackage.Create(branchA,branchPackage);
                var preparedDirectory=Path.Combine(root,"prepared-sync");
                var preparation=KnowledgeRecoveryPreparation.Prepare(branchPackage,branchBPath,preparedDirectory);
                var candidate=new SqliteKnowledgeDatabase(Path.Combine(preparedDirectory,"candidate.db"));
                var quarantine=new SqliteKnowledgeDatabase(Path.Combine(preparedDirectory,"quarantine.db"));
                var candidateEvidence=new SqlEvidenceRepository(candidate);var quarantineEvidence=new SqlEvidenceRepository(quarantine);
                Check("sync_preparation_preserves_current_and_isolates_baseline_exclusive_content",
                    candidateEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,"only-b",1))?.Text=="仅乙存在" &&
                    candidateEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,"only-a",1)) is null &&
                    quarantineEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,"only-a",1))?.Text=="仅甲存在" &&
                    candidateEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,"same-revision",1))?.Text=="分支乙的合成秘密" &&
                    quarantineEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,"same-revision",1))?.Text=="分支甲的合成秘密");
                Check("sync_preparation_applies_union_of_branch_deletions",sharedRecords.All(r=>
                    candidateEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,r.Id,1)) is null &&
                    quarantineEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,r.Id,1)) is null));
                var candidateBlocked=false;var quarantineBlocked=false;
                try{KnowledgeRecovery.DemandActivationReady(candidate);}catch(InvalidOperationException){candidateBlocked=true;}
                try{KnowledgeRecovery.DemandActivationReady(quarantine);}catch(InvalidOperationException){quarantineBlocked=true;}
                Check("sync_preparation_gates_both_outputs_and_keeps_sources_unchanged",candidateBlocked && quarantineBlocked &&
                    new SqlEvidenceRepository(branchA).Read(archiveAccess,new(archiveAccess.MemoryNamespace,sharedRecords[1].Id,1)) is not null &&
                    new SqlEvidenceRepository(branchB).Read(archiveAccess,new(archiveAccess.MemoryNamespace,sharedRecords[0].Id,1)) is not null &&
                    !Directory.GetFiles(preparedDirectory,"*working*").Any());
                var overwritePreparationRejected=false;
                try{KnowledgeRecoveryPreparation.Prepare(branchPackage,branchBPath,preparedDirectory);}catch(IOException){overwritePreparationRejected=true;}
                Check("sync_preparation_never_overwrites_review_package",overwritePreparationRejected && File.Exists(Path.Combine(preparedDirectory,"report.json")));
                var absentFactSourceRejected=false;
                try{KnowledgeRecoveryFactReview.Stage(candidate,quarantine.FilePath,archiveAccess,"recovery-negative",1);}catch(InvalidDataException){absentFactSourceRejected=true;}
                Check("recovery_fact_requires_adopted_matching_original",absentFactSourceRejected);
                var factOriginalReview=KnowledgeRecoveryAdoption.Review(candidate,quarantine.FilePath,archiveAccess,"synthetic-reviewer",[factOriginal.Id]);
                KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,factOriginalReview.Decision);
                var factStage=KnowledgeRecoveryFactReview.Stage(candidate,quarantine.FilePath,archiveAccess,"recovery-negative",1);
                var repeatedStage=KnowledgeRecoveryFactReview.Stage(candidate,quarantine.FilePath,archiveAccess,"recovery-negative",1);
                var recoveryAdminOutput=new StringWriter();
                var recoveryAdminExit=await KnowledgeAdminCommand.RunAsync(["recover-fact","--db",candidate.FilePath,"--user","archive","--persona","fixture","--scope","desktop","--quarantine",quarantine.FilePath,"--assertion","recovery-negative","--assertion-revision","1"],recoveryAdminOutput);
                Check("recovery_fact_actual_admin_handler_needs_no_model",recoveryAdminExit==0 && recoveryAdminOutput.ToString().Contains(factStage.JobId));
                var candidateFacts=new HuTao.Knowledge.Facts.SqlAssertionRepository(candidate);
                Check("recovery_fact_stage_is_idempotent_and_not_published",repeatedStage.AlreadyStaged && repeatedStage.JobId==factStage.JobId && candidateFacts.Query(archiveAccess,archiveAccess.MemoryNamespace,new("user","prefers")).Count==0);
                var reviewRequired=false;
                try{new HuTao.Knowledge.Graph.ExtractionPublisher(candidate).Publish(archiveAccess,factStage.JobId,new HashSet<string>{"recovery-negative"},"synthetic-reviewer");}catch(InvalidDataException){reviewRequired=true;}
                Check("recovery_fact_requires_explicit_candidate_approval",reviewRequired);
                new HuTao.Knowledge.Graph.ExtractionPublisher(candidate).Publish(archiveAccess,factStage.JobId,new HashSet<string>{"recovery-negative"},"synthetic-reviewer",approveCandidates:true);
                var recoveredNegative=candidateFacts.Query(archiveAccess,archiveAccess.MemoryNamespace,new("user","prefers")).Single();
                Check("recovery_fact_preserves_negation_and_exact_source",recoveredNegative.Polarity==-1 && recoveredNegative.Literal=="咖啡" && recoveredNegative.Sources.Single().Reference.Id==factOriginal.Id);
                Check("recovery_fact_preserves_valid_time_interval",recoveredNegative.ValidFrom==recoveryValidFrom && recoveredNegative.ValidTo==recoveryValidTo);
                new KnowledgeDeletionService(candidate).Forget(archiveAccess,archiveAccess.MemoryNamespace,[factOriginal.Id]);
                Check("recovery_published_fact_obeys_forgetting",candidateFacts.Query(archiveAccess,archiveAccess.MemoryNamespace,new("user","prefers")).Count==0);
                var catReview=KnowledgeRecoveryAdoption.Review(candidate,quarantine.FilePath,archiveAccess,"synthetic-reviewer",[catOriginal.Id]);
                KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,catReview.Decision);
                var ungroundedEntityRejected=false;
                try{KnowledgeRecoveryFactReview.Stage(candidate,quarantine.FilePath,archiveAccess,"ungrounded-care",1);}catch(InvalidDataException){ungroundedEntityRejected=true;}
                Check("recovery_entity_without_exact_name_mention_rejected",ungroundedEntityRejected);
                var catStage=KnowledgeRecoveryFactReview.Stage(candidate,quarantine.FilePath,archiveAccess,"recovery-care",1);
                using(var c=candidate.Open())using(var command=c.CreateCommand())
                {command.CommandText="SELECT COUNT(*) FROM entities WHERE entity_id='recovery-cat'";Check("recovery_entity_stays_proposal_until_publication",Convert.ToInt64(command.ExecuteScalar())==0);}
                candidateFacts.RegisterEntity(archiveAccess,archiveAccess.MemoryNamespace,"recovery-cat","animal","不同名字");
                var lateEntityConflictRejected=false;
                try{new HuTao.Knowledge.Graph.ExtractionPublisher(candidate).Publish(archiveAccess,catStage.JobId,new HashSet<string>{"recovery-care"},"synthetic-reviewer",approveCandidates:true);}catch(InvalidDataException){lateEntityConflictRejected=true;}
                Check("recovery_entity_identity_conflict_at_publication_rejected",lateEntityConflictRejected && candidateFacts.Query(archiveAccess,archiveAccess.MemoryNamespace,new("user","cares_for")).Count==0);
                using(var c=candidate.Open())using(var command=c.CreateCommand()){command.CommandText="DELETE FROM entities WHERE entity_id='recovery-cat'";command.ExecuteNonQuery();}
                new HuTao.Knowledge.Graph.ExtractionPublisher(candidate).Publish(archiveAccess,catStage.JobId,new HashSet<string>{"recovery-care"},"synthetic-reviewer",approveCandidates:true);
                Check("recovery_missing_entity_and_fact_publish_atomically",candidateFacts.Query(archiveAccess,archiveAccess.MemoryNamespace,new("user","cares_for")).Single().ObjectId=="recovery-cat");
                new KnowledgeDeletionService(candidate).Forget(archiveAccess,archiveAccess.MemoryNamespace,[catOriginal.Id]);
                using(var c=candidate.Open())using(var command=c.CreateCommand())
                {command.CommandText="SELECT canonical_name FROM entities WHERE entity_id='recovery-cat'";Check("recovery_entity_name_scrubbed_with_its_source",command.ExecuteScalar() as string=="");}
                var sourceStamp=quarantineEvidence.ReadStamps(archiveAccess,[archiveAccess.MemoryNamespace])[archiveAccess.MemoryNamespace];
                var targetStamp=candidateEvidence.ReadStamps(archiveAccess,[archiveAccess.MemoryNamespace])[archiveAccess.MemoryNamespace];
                var adoptionDecision=new RecoveryAdoptionDecision("synthetic-reviewer",archiveAccess.MemoryNamespace,[adoptionOriginal.Id],sourceStamp.CommitSequence,sourceStamp.PrivacyEpoch,targetStamp.CommitSequence,targetStamp.PrivacyEpoch);
                var adoptionReview=KnowledgeRecoveryAdoption.Review(candidate,quarantine.FilePath,archiveAccess,"synthetic-reviewer",[adoptionOriginal.Id]);
                adoptionDecision=adoptionReview.Decision;
                Check("recovery_review_returns_exact_original_and_current_versions",adoptionReview.Turns.Single().Text==adoptionOriginal.Text && !adoptionReview.Turns.Single().CandidateHasId &&
                    adoptionReview.Decision.SourceCommitSequence==sourceStamp.CommitSequence && adoptionReview.Decision.TargetCommitSequence==targetStamp.CommitSequence);
                var missingDependencyReview=KnowledgeRecoveryAdoption.Review(candidate,quarantine.FilePath,archiveAccess,"synthetic-reviewer",[adoptionAnswer.Id]);
                var missingDependencyRejected=false;
                try{KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,missingDependencyReview.Decision);}catch(InvalidDataException){missingDependencyRejected=true;}
                Check("recovery_assistant_missing_source_rejected_without_partial_insert",missingDependencyRejected && candidateEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,adoptionAnswer.Id,1)) is null && missingDependencyReview.Turns.Single().Dependencies.Single().SourceId==adoptionOriginal.Id);
                var divergentReview=KnowledgeRecoveryAdoption.Review(candidate,quarantine.FilePath,archiveAccess,"synthetic-reviewer",[divergentAnswer.Id]);
                var divergentDependencyRejected=false;
                try{KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,divergentReview.Decision);}catch(InvalidDataException){divergentDependencyRejected=true;}
                Check("recovery_same_source_id_revision_different_content_rejected",divergentDependencyRejected && candidateEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,divergentAnswer.Id,1)) is null);
                var staleAdoptionRejected=false;
                try{KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,adoptionDecision with {TargetCommitSequence=targetStamp.CommitSequence+1});}catch(InvalidOperationException){staleAdoptionRejected=true;}
                Check("recovery_adoption_rejects_stale_review",staleAdoptionRejected && candidateEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,adoptionOriginal.Id,1)) is null);
                var staleMemoryReviewRejected=false;
                try{KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,adoptionDecision with {SourceMemoryRevision=adoptionDecision.SourceMemoryRevision+1});}catch(InvalidOperationException){staleMemoryReviewRejected=true;}
                Check("recovery_adoption_checks_memory_state_revision",staleMemoryReviewRejected);
                var conflictingAdoptionRejected=false;
                try{KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,adoptionDecision with {EvidenceIds=[adoptionOriginal.Id,"same-revision"]});}catch(InvalidDataException){conflictingAdoptionRejected=true;}
                Check("recovery_adoption_conflicting_batch_is_atomic",conflictingAdoptionRejected && candidateEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,adoptionOriginal.Id,1)) is null);
                using(var c=candidate.Open())using(var command=c.CreateCommand())
                {command.CommandText="CREATE TRIGGER fail_adoption BEFORE INSERT ON recovery_adoptions BEGIN SELECT RAISE(ABORT,'fixture'); END";command.ExecuteNonQuery();}
                var adoptionRolledBack=false;
                try{KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,adoptionDecision);}catch(Microsoft.Data.Sqlite.SqliteException){adoptionRolledBack=true;}
                Check("recovery_adoption_late_failure_rolls_back_original_and_fts",adoptionRolledBack && candidateEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,adoptionOriginal.Id,1)) is null && !candidateEvidence.Search(archiveAccess,"待审阅采纳").Any(e=>e.Reference.Id==adoptionOriginal.Id));
                using(var c=candidate.Open())using(var command=c.CreateCommand()){command.CommandText="DROP TRIGGER fail_adoption";command.ExecuteNonQuery();}
                var adopted=KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,adoptionDecision);
                var adoptionReplay=KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,adoptionDecision);
                Check("recovery_adopts_original_with_stable_id_and_search",adopted.Adopted==1 && adoptionReplay.Replay &&
                    candidateEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,adoptionOriginal.Id,1))?.Text==adoptionOriginal.Text &&
                    candidateEvidence.Search(archiveAccess,"待审阅采纳").Any(e=>e.Reference.Id==adoptionOriginal.Id));
                using(var c=candidate.Open())using(var command=c.CreateCommand())
                {command.CommandText="SELECT status FROM turn_ingestion WHERE evidence_id=@id";command.Parameters.AddWithValue("@id",adoptionOriginal.Id);Check("recovery_adoption_does_not_automatically_publish_old_facts",command.ExecuteScalar() as string=="deferred");}
                var answerReview=KnowledgeRecoveryAdoption.Review(candidate,quarantine.FilePath,archiveAccess,"synthetic-reviewer",[adoptionAnswer.Id]);
                KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,answerReview.Decision);
                var restoredAnswer=new SqlConversationMemoryRepository(candidate,archiveAccess).Load().State.Records.Single(r=>r.Id==adoptionAnswer.Id);
                Check("recovery_assistant_preserves_exact_provenance",restoredAnswer.Sources.SequenceEqual([adoptionOriginal.Id]) && restoredAnswer.SourceRevisions[adoptionOriginal.Id]==1);
                new KnowledgeDeletionService(candidate).Forget(archiveAccess,archiveAccess.MemoryNamespace,[adoptionOriginal.Id]);
                Check("recovery_assistant_forgetting_propagates_after_adoption",candidateEvidence.Read(archiveAccess,new(archiveAccess.MemoryNamespace,adoptionAnswer.Id,1)) is null);
                var batchDirectory=Path.Combine(root,"prepared-batch");
                KnowledgeRecoveryPreparation.Prepare(branchPackage,branchBPath,batchDirectory);
                var batchCandidate=new SqliteKnowledgeDatabase(Path.Combine(batchDirectory,"candidate.db"));
                var batchQuarantine=Path.Combine(batchDirectory,"quarantine.db");
                var batchReview=KnowledgeRecoveryAdoption.Review(batchCandidate,batchQuarantine,archiveAccess,"synthetic-reviewer",[adoptionAnswer.Id,adoptionOriginal.Id]);
                var batchResult=KnowledgeRecoveryAdoption.Adopt(batchCandidate,batchQuarantine,archiveAccess,batchReview.Decision);
                Check("recovery_dependency_batch_is_order_independent",batchResult.Adopted==2 && new SqlConversationMemoryRepository(batchCandidate,archiveAccess).Load().State.Records.Single(r=>r.Id==adoptionAnswer.Id).SourceRevisions[adoptionOriginal.Id]==1);
                using(var c=batchCandidate.Open())using(var command=c.CreateCommand())
                {command.CommandText="SELECT COUNT(*) FROM turn_ingestion WHERE evidence_id=@id";command.Parameters.AddWithValue("@id",adoptionAnswer.Id);Check("recovery_assistant_never_queued_for_user_fact_extraction",Convert.ToInt64(command.ExecuteScalar())==0);}
                var forgottenAdoptionRejected=false;
                try{KnowledgeRecoveryAdoption.Adopt(candidate,quarantine.FilePath,archiveAccess,adoptionDecision);}catch(InvalidDataException){forgottenAdoptionRejected=true;}
                Check("recovery_adoption_replay_cannot_bypass_later_forgetting",forgottenAdoptionRejected);
                var restoredRepository=new SqlConversationMemoryRepository(restoredDb,archiveAccess,100);
                Check("versioned_backup_restores_active_archive_and_privacy",restoredManifest==manifest && restoredRepository.Load().State.Records.Count==100 && restoredRepository.LoadArchived().State.Records.Count==5 &&
                    new SqlEvidenceRepository(restoredDb).Read(archiveAccess,new(archiveAccess.MemoryNamespace,anchor.Id,1)) is null &&
                    new SqlEvidenceRepository(restoredDb).PrivacyEpochs(archiveAccess)[archiveAccess.MemoryNamespace]==new SqlEvidenceRepository(archiveDb).PrivacyEpochs(archiveAccess)[archiveAccess.MemoryNamespace]);
                var overwriteRejected=false;try{KnowledgeBackupPackage.Restore(package,restoredPath);}catch(IOException){overwriteRejected=true;}
                Check("backup_restore_never_overwrites_existing_database",overwriteRejected);
                var forgottenAfterBackup=archiveRepository.Load().State.Records.First();
                new KnowledgeDeletionService(archiveDb).Forget(archiveAccess,archiveAccess.MemoryNamespace,[forgottenAfterBackup.Id]);
                var reconciledPath=Path.Combine(root,"archive-reconciled.db");
                var reconciled=KnowledgeBackupPackage.RestoreWithDeletions(package,archiveDb.FilePath,reconciledPath);
                var reconciledDb=new SqliteKnowledgeDatabase(reconciledPath);
                Check("recovery_propagates_post_backup_forget",reconciled.Recovery.State=="pending-final-sync" && reconciled.Recovery.EvidenceDeleted==1 &&
                    new SqlEvidenceRepository(reconciledDb).Read(archiveAccess,new(archiveAccess.MemoryNamespace,forgottenAfterBackup.Id,1)) is null &&
                    new SqlConversationMemoryRepository(reconciledDb,archiveAccess,100).Load().State.Records.Count==99);
                using(var c=reconciledDb.Open())
                using(var command=c.CreateCommand())
                {
                    command.CommandText="SELECT text FROM evidence_units WHERE namespace_id=@n AND evidence_id=@id";
                    command.Parameters.AddWithValue("@n",archiveAccess.MemoryNamespace);command.Parameters.AddWithValue("@id",forgottenAfterBackup.Id);
                    Check("recovery_scrubs_forgotten_body",command.ExecuteScalar() as string=="");
                }
                var laterForget=archiveRepository.Load().State.Records.First();
                new KnowledgeDeletionService(archiveDb).Forget(archiveAccess,archiveAccess.MemoryNamespace,[laterForget.Id]);
                var recoveryBlocked=false;
                try{await using var pendingRuntime=new KnowledgeRuntime(new(KnowledgeRuntimeMode.Sql,reconciledPath,"archive"),"fixture","desktop",null);}
                catch(InvalidOperationException){recoveryBlocked=true;}
                Check("recovery_snapshot_does_not_authorize_activation_after_later_forget",recoveryBlocked);
                var unrelated=new SqliteKnowledgeDatabase(Path.Combine(root,"unrelated-authority.db"));
                var badPreparationPath=Path.Combine(root,"bad-preparation");var badPreparationRejected=false;
                try{KnowledgeRecoveryPreparation.Prepare(branchPackage,unrelated.FilePath,badPreparationPath);}catch(InvalidDataException){badPreparationRejected=true;}
                Check("sync_preparation_failure_publishes_nothing_and_cleans_owned_staging",badPreparationRejected && !Directory.Exists(badPreparationPath) && Directory.GetDirectories(root,"bad-preparation.preparing-*").Length==0);
                var rejectedRecoveryPath=Path.Combine(root,"rejected-recovery.db");var unrelatedRejected=false;
                try{KnowledgeBackupPackage.RestoreWithDeletions(package,unrelated.FilePath,rejectedRecoveryPath);}catch(InvalidDataException){unrelatedRejected=true;}
                Check("recovery_rejects_missing_authority_namespace_and_cleans_staging",unrelatedRejected && !File.Exists(rejectedRecoveryPath) && Directory.GetFiles(root,"rejected-recovery.db.*").Length==0);
                unrelated.EnsureNamespace(archiveAccess.MemoryNamespace);
                using(var c=unrelated.Open())using(var command=c.CreateCommand())
                {command.CommandText="UPDATE namespaces SET privacy_epoch=999; UPDATE namespace_state SET commit_seq=999";command.ExecuteNonQuery();}
                var sameNamesRejected=false;
                try{KnowledgeBackupPackage.RestoreWithDeletions(package,unrelated.FilePath,rejectedRecoveryPath);}catch(InvalidDataException){sameNamesRejected=true;}
                Check("recovery_rejects_unrelated_lineage_despite_matching_scope_and_higher_counters",sameNamesRejected && !File.Exists(rejectedRecoveryPath));
                var pendingSourceRejected=false;
                try{KnowledgeBackupPackage.RestoreWithDeletions(package,reconciledPath,rejectedRecoveryPath);}catch(InvalidDataException){pendingSourceRejected=true;}
                Check("pending_recovery_cannot_be_authority",pendingSourceRejected && !File.Exists(rejectedRecoveryPath));
                var oldFormatPath=Path.Combine(root,"pre-lineage.db");archiveDb.Backup(oldFormatPath);
                using(var c=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldFormatPath};Pooling=False"))using(var command=c.CreateCommand())
                {c.Open();command.CommandText="DROP TABLE recovery_adoptions; DROP TABLE database_identity; DELETE FROM schema_migrations WHERE version>=9";command.ExecuteNonQuery();}
                var oldFormatPackage=Path.Combine(root,"pre-lineage.zip");KnowledgeBackupPackage.CreateFromFile(oldFormatPath,oldFormatPackage);
                var oldFormatRejected=false;
                try{KnowledgeBackupPackage.RestoreWithDeletions(oldFormatPackage,archiveDb.FilePath,rejectedRecoveryPath);}catch(InvalidDataException){oldFormatRejected=true;}
                Check("pre_lineage_backup_not_silently_assigned_identity",oldFormatRejected && !File.Exists(rejectedRecoveryPath));
                var oldRawPath=Path.Combine(root,"pre-lineage-raw.db");
                Check("pre_lineage_backup_still_restores_for_inspection",KnowledgeBackupPackage.Restore(oldFormatPackage,oldRawPath).SchemaVersion==8);
                using(var zip=System.IO.Compression.ZipFile.Open(package,System.IO.Compression.ZipArchiveMode.Update))
                {
                    zip.GetEntry("manifest.json")!.Delete();
                    using var stream=zip.CreateEntry("manifest.json").Open();
                    System.Text.Json.JsonSerializer.Serialize(stream,manifest with {Sha256=new string('0',64)});
                }
                var corruptDestination=Path.Combine(root,"corrupt-restore.db");var corruptRejected=false;
                try{KnowledgeBackupPackage.Restore(package,corruptDestination);}catch(InvalidDataException){corruptRejected=true;}
                Check("backup_manifest_mismatch_never_promotes_restore",corruptRejected && !File.Exists(corruptDestination) && Directory.GetFiles(root,"corrupt-restore.db.restore-*").Length==0);
            }
            {
            var queueDb=new SqliteKnowledgeDatabase(Path.Combine(root,"queue.db"));
            var queueAccess=KnowledgeAccessContext.ForSession("queue","fixture","desktop",[]);
            var queueStore=new ConversationMemoryStore(Path.Combine(root,"unused.json"),repository:new SqlConversationMemoryRepository(queueDb,queueAccess));
            var queuedUser=queueStore.ObserveTurn("user","我晚上喝温水。",DateTimeOffset.Now)!;
            queueStore.ObserveTurn("assistant","合成回答",DateTimeOffset.Now);
            var queue=new HuTao.Knowledge.Graph.TurnIngestionQueue(new SqliteKnowledgeDatabase(queueDb.FilePath));
            var lease=queue.Claim(queueAccess)!;
            Check("ingestion_queue_survives_reopen_user_only",lease.Reference.Id==queuedUser.Id && queue.Claim(queueAccess) is null);
            Check("ingestion_queue_rejects_wrong_lease",!queue.Complete(queueAccess,lease with {Token="wrong"},"completed"));
            new KnowledgeDeletionService(queueDb).Forget(queueAccess,queueAccess.MemoryNamespace,[queuedUser.Id]);
            Check("forgotten_ingestion_lease_cannot_complete",!queue.Complete(queueAccess,lease,"completed") && queue.Claim(queueAccess) is null);
            var anotherUser=queueStore.ObserveTurn("user","我早上喝茶。",DateTimeOffset.Now)!;
            var anotherLease=queue.Claim(queueAccess)!;
            Check("ingestion_queue_terminal_once",anotherLease.Reference.Id==anotherUser.Id && queue.Complete(queueAccess,anotherLease,"deferred") && !queue.Complete(queueAccess,anotherLease,"completed") && queue.Claim(queueAccess) is null);
            queueStore.ObserveTurn("user","我晚上只喝牛奶。",DateTimeOffset.Now);
            var recovered=new KnowledgeRuntime(new(KnowledgeRuntimeMode.Sql,queueDb.FilePath,UserId:"queue"),"fixture","desktop",null);
            recovered.Activate();
            try
            {
                var timer=System.Diagnostics.Stopwatch.StartNew();
                while(timer.Elapsed<TimeSpan.FromSeconds(5) && new HuTao.Knowledge.Facts.SqlAssertionRepository(queueDb).Query(queueAccess,queueAccess.MemoryNamespace,new("user","drinks")).Count==0)
                    await Task.Delay(25);
                Check("runtime_startup_recovers_committed_unprocessed_turn",new HuTao.Knowledge.Facts.SqlAssertionRepository(queueDb).Query(queueAccess,queueAccess.MemoryNamespace,new("user","drinks")).Any(f=>f.Literal=="牛奶"));
            }
            finally{await recovered.DisposeAsync();}
            Check("sql_recovery_worker_stops_on_dispose",recovered.WorkerStopped);
            var disposeDatabase=new SqliteKnowledgeDatabase(Path.Combine(root,"dispose-wait.db"));
            var disposingRuntime=new KnowledgeRuntime(new(KnowledgeRuntimeMode.Sql,disposeDatabase.FilePath,"dispose"),"fixture","desktop",null);
            var workerBarrier=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(KnowledgeRuntime).GetField("_worker",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(disposingRuntime,workerBarrier.Task);
            var firstDispose=disposingRuntime.DisposeAsync().AsTask();
            var secondDispose=disposingRuntime.DisposeAsync().AsTask();
            Check("concurrent_dispose_waits_for_same_worker",ReferenceEquals(firstDispose,secondDispose) && !secondDispose.IsCompleted);
            workerBarrier.SetResult();await Task.WhenAll(firstDispose,secondDispose).WaitAsync(TimeSpan.FromSeconds(5));
            Check("concurrent_dispose_finishes_after_worker",disposingRuntime.WorkerStopped);
            var failedCleanup=new KnowledgeRuntime(new(KnowledgeRuntimeMode.Sql,disposeDatabase.FilePath,"dispose-failure"),"fixture","desktop",null);
            typeof(KnowledgeRuntime).GetField("_worker",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(failedCleanup,Task.FromException(new IOException("Synthetic worker failure.")));
            var stop=(CancellationTokenSource)typeof(KnowledgeRuntime).GetField("_stop",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(failedCleanup)!;
            var failedTask=failedCleanup.DisposeAsync().AsTask();
            var reported=false;try {await failedTask;}catch(AggregateException ex){reported=ex.InnerExceptions.Any(e=>e is IOException);}
            var released=false;try {stop.Cancel();}catch(ObjectDisposedException){released=true;}
            Check("failed_worker_still_releases_cancellation_source",reported && released);
            await failedCleanup.DisposeAsync();
            Check("cleanup_retry_preserves_history_without_blocking_released_resources",failedTask.IsFaulted && failedCleanup.CleanupFailures.Any(e=>e.Message=="Synthetic worker failure."));
            var dualCleanup=new KnowledgeRuntime(new(KnowledgeRuntimeMode.Sql,disposeDatabase.FilePath,"dual-failure"),"fixture","desktop",null);
            typeof(KnowledgeRuntime).GetField("_worker",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(dualCleanup,Task.FromException(new IOException("Synthetic worker failure.")));
            var dualPath=Path.Combine(root,"dual-failure-memory.json");
            var dualStore=new ConversationMemoryStore(dualPath);
            dualStore.ObserveTurn("user","合成的待保存记忆",DateTimeOffset.Now);
            dualStore.Flush(throwOnError:true);
            dualStore.ObserveTurn("user","第二条待保存记忆",DateTimeOffset.Now);
            var dualFence=new KnowledgeAuthorityFence(Path.Combine(root,"dual-authority.json"));
            using var dualLifetime=dualFence.RegisterLegacyRuntime();
            var dualRuntime=new AgentRuntime(null!,null!,null!,null!,null,null!,null!,[],MemoryStore:dualStore,Knowledge:dualCleanup,LegacyLifetime:dualLifetime);
            using(var lockedMemory=new FileStream(dualPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                AggregateException? dualFailure=null;
                try {await dualRuntime.DisposeAsync();}catch(AggregateException ex){dualFailure=ex;}
                Check("runtime_preserves_memory_and_cleanup_failures",dualFailure is not null && dualFailure.InnerExceptions.Count==2 &&
                    dualFailure.InnerExceptions[0] is IOException or UnauthorizedAccessException &&
                    dualFailure.Flatten().InnerExceptions.Any(e=>e.Message=="Synthetic worker failure."));
                var stillOwned=false;try{using var takeover=dualFence.RequireNoLegacyRuntime();}catch(IOException){stillOwned=true;}
                Check("failed_runtime_flush_keeps_legacy_lifetime",stillOwned);
            }
            await dualRuntime.DisposeAsync();
            Check("failed_runtime_memory_can_be_saved_after_unlock",new ConversationMemoryStore(dualPath).Count==2);
            var driverCleanup=new KnowledgeRuntime(new(KnowledgeRuntimeMode.Sql,disposeDatabase.FilePath,"driver-failure"),"fixture","desktop",null);
            var driver=System.Reflection.DispatchProxy.Create<Neo4j.Driver.IDriver,DisposalDriverProxy>();
            var driverProbe=(DisposalDriverProxy)(object)driver;
            typeof(KnowledgeRuntime).GetField("_driver",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(driverCleanup,driver);
            typeof(KnowledgeRuntime).GetField("_worker",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(driverCleanup,Task.FromException(new IOException("First worker failure.")));
            typeof(KnowledgeRuntime).GetField("_recovery",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(driverCleanup,Task.FromException(new IOException("Second worker failure.")));
            var cleanupQueue=new PendingRuntimeCleanup();
            AggregateException? driverError=null;
            try{await cleanupQueue.RetireAsync(driverCleanup);}catch(AggregateException ex){driverError=ex;}
            Check("cleanup_reports_both_workers_and_driver",driverError?.Flatten().InnerExceptions.Count==3 && driverProbe.Calls==1);
            await cleanupQueue.RetryAsync();
            await cleanupQueue.RetryAsync();
            Check("cleanup_queue_retries_only_unreleased_driver",driverProbe.Calls==2 && driverCleanup.CleanupFailures.Count==3 && driverCleanup.WorkerStopped);
            var sqlFence=new KnowledgeAuthorityFence(Path.Combine(root,"sql-lifetime-authority.json"));
            IDisposable sqlLifetime;
            using(sqlFence.Acquire(legacy:false))sqlLifetime=sqlFence.RegisterSqlRuntime();
            var sqlCleanup=new KnowledgeRuntime(new(KnowledgeRuntimeMode.Sql,disposeDatabase.FilePath,"sql-lifetime"),"fixture","desktop",null);
            typeof(KnowledgeRuntime).GetField("_worker",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(sqlCleanup,Task.FromException(new IOException("Synthetic SQL shutdown failure.")));
            var sqlOwner=new AgentRuntime(null!,null!,null!,null!,null,null!,null!,[],Knowledge:sqlCleanup,SqlLifetime:sqlLifetime);
            var sqlCompeting=new KnowledgeAuthorityFence(Path.Combine(root,"sql-lifetime-authority.json"));
            bool SqlStillAlive()
            {
                try{using var exclusive=sqlCompeting.RequireNoSqlRuntime();return false;}catch(IOException){return true;}
            }
            try
            {
                Check("sql_live_runtime_blocks_exclusive_rollback",SqlStillAlive());
                try{await sqlOwner.DisposeAsync();}catch(AggregateException){}
                Check("sql_cleanup_failure_retains_lifetime",SqlStillAlive());
                await sqlOwner.DisposeAsync();
                Check("sql_cleanup_retry_releases_lifetime",!SqlStillAlive());
                using var exclusive=sqlCompeting.RequireNoSqlRuntime();
                var registrationBlocked=false;
                try{using var denied=sqlFence.RegisterSqlRuntime();}catch(IOException){registrationBlocked=true;}
                Check("sql_exclusive_rollback_blocks_runtime_registration",registrationBlocked);
            }
            finally{sqlLifetime.Dispose();}
            var expiredUser=queueStore.ObserveTurn("user","我早上喝牛奶。",DateTimeOffset.Now)!;
            var firstLease=queue.Claim(queueAccess)!;
            void ExpireLease()
            {
                using var connection=queueDb.Open();using var command=connection.CreateCommand();
                command.CommandText="UPDATE turn_ingestion SET lease_until=0 WHERE namespace_id=@n AND evidence_id=@id";
                command.Parameters.AddWithValue("@n",queueAccess.MemoryNamespace);command.Parameters.AddWithValue("@id",expiredUser.Id);command.ExecuteNonQuery();
            }
            ExpireLease();var secondLease=queue.Claim(queueAccess)!;
            Check("reclaimed_ingestion_rejects_old_worker",secondLease.Reference.Id==expiredUser.Id && secondLease.Token!=firstLease.Token && !queue.Complete(queueAccess,firstLease,"completed"));
            ExpireLease();var thirdLease=queue.Claim(queueAccess)!;ExpireLease();
            Check("expired_attempt_limit_leaves_ready_batch",queue.ReadyIds(queueAccess).Count==0 && queue.Claim(queueAccess) is null && !queue.Complete(queueAccess,thirdLease,"completed"));
            var editedUser=queueStore.ObserveTurn("user","我晚上喝豆浆。",DateTimeOffset.Now)!;
            var editLease=queue.Claim(queueAccess)!;
            new SqlEvidenceRepository(queueDb).Put(queueAccess.MemoryNamespace,new(editedUser.Id,"已改写的合成原文","user","memory:"+editedUser.Id,"memory",ObservedAt:editedUser.ObservedAt));
            Check("source_revision_change_cancels_lease_and_ready_entry",!queue.Complete(queueAccess,editLease,"completed") && queue.ReadyIds(queueAccess).Count==0);
            }
            var authority=Path.Combine(root,"fence","authority.json");
            var fence=new KnowledgeAuthorityFence(authority);
            var competing=new KnowledgeAuthorityFence(authority);
            using(var lease=fence.Acquire(legacy:true))
            {
                var rejected=false;
                try{using var other=competing.Acquire(legacy:false);}catch(IOException){rejected=true;}
                Check("independent_handle_cannot_activate_during_json_access",rejected);
            }
            var memoryPath=Path.Combine(root,"fence","memory.json");
            var delayedAuthority=Path.Combine(root,"delayed","authority.json");
            var delayedFence=new KnowledgeAuthorityFence(delayedAuthority);
            IDisposable delayedLifetime;
            using(delayedFence.Acquire(legacy:true))delayedLifetime=delayedFence.RegisterLegacyRuntime();
            var delayedPath=Path.Combine(root,"delayed","memory.json");
            var delayedStore=new ConversationMemoryStore(delayedPath,accessLease:()=>delayedFence.Acquire(legacy:true));
            var delayedTurn=delayedStore.ObserveTurn("user","我喜欢合成测试中的温水",DateTimeOffset.Now)!;
            var delayedLlm=new DelayedConsolidationLlm(delayedTurn.Id);
            var pendingConsolidation=new MemoryConsolidator(delayedStore,delayedLlm).RunAsync(DateTimeOffset.Now);
            try
            {
                await delayedLlm.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                delayedStore.FlushAndRetire(delayedLifetime.Dispose);
                var beforeLateCompletion=File.ReadAllBytes(delayedPath);
                delayedLlm.Release.TrySetResult();
                var lateResult=await pendingConsolidation.WaitAsync(TimeSpan.FromSeconds(5));
                var reopened=new ConversationMemoryStore(delayedPath);
                Check("late_consolidation_after_retirement_does_not_write",lateResult.Path=="failed" && beforeLateCompletion.SequenceEqual(File.ReadAllBytes(delayedPath)));
                Check("late_consolidation_preserves_pending_cursor",reopened.Count==1 && reopened.UnconsolidatedTurnCount==1 && reopened.LastConsolidatedAt is null);
            }
            finally {delayedLlm.Release.TrySetResult();await pendingConsolidation;delayedLifetime.Dispose();}
            using(var access=fence.Acquire(legacy:true))
            {
                var lifecycleFence=new KnowledgeAuthorityFence(authority);
                using(var live=lifecycleFence.RegisterLegacyRuntime())
                {
                    var blocked=false;
                    try {using var takeover=competing.RequireNoLegacyRuntime();}catch(IOException){blocked=true;}
                    Check("live_legacy_registration_blocks_sql_takeover",blocked);
                }
                using var stopped=competing.RequireNoLegacyRuntime();
                Check("released_legacy_registration_allows_takeover",true);
                Check("retired_legacy_fence_never_becomes_current_again",!lifecycleFence.LegacyIsCurrent());
            }
            var store=new ConversationMemoryStore(memoryPath,accessLease:()=>fence.Acquire(legacy:true));
            var important=new HuTao.Dialogue.Storage.ImportantMemoryStore(Path.Combine(root,"fence","items.json"),accessLease:()=>fence.Acquire(legacy:true));
            store.ObserveTurn("user","合成旧记忆",DateTimeOffset.Now);store.Flush();
            var before=File.ReadAllBytes(memoryPath);
            using(var activation=competing.Acquire(legacy:false))File.WriteAllText(authority,"{}");
            var readRejected=false;var writeRejected=false;var importantRejected=false;
            try{store.Snapshot();}catch(InvalidOperationException){readRejected=true;}
            try{store.ObserveTurn("user","接管后不应写入",DateTimeOffset.Now.AddMinutes(1));}catch(InvalidOperationException){writeRejected=true;}
            try{important.PreparePrompt("记住不要写入",false,DateTimeOffset.Now);}catch(InvalidOperationException){importantRejected=true;}
            Check("deposed_stores_refuse_reads_and_writes",readRejected && writeRejected && importantRejected && before.SequenceEqual(File.ReadAllBytes(memoryPath)));
            var callCounts=new Dictionary<KnowledgeRuntimeMode,int>();
            foreach(var mode in Enum.GetValues<KnowledgeRuntimeMode>())
            {
                var directory=Path.Combine(root,mode.ToString());Directory.CreateDirectory(Path.Combine(directory,"persona"));
                File.WriteAllText(Path.Combine(directory,"persona","system-prompt.md"),"你是测试角色，只使用给出的合成资料。");
                var db=new SqliteKnowledgeDatabase(Path.Combine(directory,"knowledge.db"));var access=KnowledgeAccessContext.ForSession("local","fixture","desktop",[]);
                new SqlEvidenceRepository(db).Put(access.MemoryNamespace,new("source","我晚上只喝温水。","user","source","turn"));
                var definition=new CharacterDefinition("fixture","测试角色","","","persona","","","missing.json","你好","","忙碌",ImportantMemoryFile:"data/items.json",Tools:["document_reader"]);
                var sql=mode is KnowledgeRuntimeMode.Sql or KnowledgeRuntimeMode.SqlGraph;var llm=new FixtureLlm(sql);
                var options=new KnowledgeRuntimeOptions(mode,db.FilePath,GraphUri:mode==KnowledgeRuntimeMode.SqlGraph?"bolt://127.0.0.1:1":null);
                var factory=new AgentRuntimeFactory(directory,knowledgeOptions:options,llmFactory:_=>llm);
                if(mode==KnowledgeRuntimeMode.SqlGraph)
                {
                    var pending=new KnowledgeRuntime(options,"pending","desktop",null);
                    Check("unactivated_runtime_has_no_worker",pending.WorkerStopped);
                    await pending.DisposeAsync();
                    var rejectedActivation=false;
                    try{pending.Activate();}catch(ObjectDisposedException){rejectedActivation=true;}
                    Check("disposed_runtime_cannot_start_worker",rejectedActivation && pending.WorkerStopped);
                }
                await using(var runtime=factory.Create(definition))
                {
                    if(sql)
                    {
                        var result=await ((ReactAgent)runtime.Agent).GenerateTextAsync("晚上喝什么？",false);
                        Check(mode+"_actual_factory_agent_sql",result.Recall?.Knowledge?.Evidence.Any(e=>e.Reference.Id=="source")==true && result.Reply.Contains("温水"));
                        Check(mode+"_one_sql_memory_writer",runtime.MemoryStore!.Count==2 && !File.Exists(Path.Combine(directory,"data","memory_fixture.json")));
                        var replyRecord=runtime.MemoryStore.Snapshot().Single(r=>r.Speaker=="assistant");
                        new KnowledgeDeletionService(db).Forget(access,access.MemoryNamespace,["source"]);
                        Check(mode+"_retrieved_source_forgets_generated_memory",runtime.MemoryStore.Snapshot().All(r=>r.Id!=replyRecord.Id));
                        if(mode==KnowledgeRuntimeMode.SqlGraph)
                            Check("graph_disconnected_does_not_block_sql_startup",runtime.Knowledge!.GraphHealth is "starting" or "unavailable-sql-fallback");
                        await using var room=factory.Create(definition,memoryScope:"chatroom");
                        Check(mode+"_chatroom_namespace_isolated",room.Agent.PrivacyStamp!.Namespace!=runtime.Agent.PrivacyStamp!.Namespace && room.MemoryStore!.Count==0);
                        bool? factVisibleAtFirstCall=null;
                        llm.OnCall=()=>factVisibleAtFirstCall??=new HuTao.Knowledge.Facts.SqlAssertionRepository(db).Query(access,access.MemoryNamespace,new("user","drinks")).Count==1;
                        await ((ReactAgent)runtime.Agent).GenerateTextAsync("我晚上只喝温水。",false);
                        llm.OnCall=null;
                        Check(mode+"_current_declaration_available_before_first_model_call",factVisibleAtFirstCall==true);
                        var learned=new HuTao.Knowledge.Facts.SqlAssertionRepository(db).Query(access,access.MemoryNamespace,new("user","drinks"));
                        Check(mode+"_committed_turn_automatically_publishes_fact",runtime.Knowledge!.LastIngestionStatus=="published:1" && learned.Count==1);
                        var userSource=runtime.MemoryStore.Snapshot().Last(r=>r.Speaker=="user");
                        llm.FactSourceId=userSource.Id;
                        var followup=await ((ReactAgent)runtime.Agent).GenerateTextAsync("我晚上喝什么？",false);
                        Check(mode+"_next_turn_uses_learned_sql_fact",followup.Recall?.Knowledge?.Evidence.Any(e=>e.Reference.Id==userSource.Id)==true && followup.Reply.Contains("温水") && followup.Recall.Knowledge.Trace.Any(t=>t.Contains("SqlFacts")));
                        llm.FactSourceId=null;
                        await ((ReactAgent)runtime.Agent).GenerateTextAsync("我早上喝茶。",false);
                        await ((ReactAgent)runtime.Agent).GenerateTextAsync("改一下，我现在晚上只喝牛奶。",false);
                        var corrected=new HuTao.Knowledge.Facts.SqlAssertionRepository(db).Query(access,access.MemoryNamespace,new("user","drinks"));
                        Check(mode+"_explicit_correction_preserves_other_time_dimension",runtime.Knowledge.LastIngestionStatus=="published:1" && corrected.Count==2 && corrected.Any(f=>f.Literal=="牛奶" && f.Status=="accepted") && corrected.Any(f=>f.Literal=="茶") && corrected.All(f=>f.Literal!="温水"));
                        var correctedSource=runtime.MemoryStore.Snapshot().Last(r=>r.Speaker=="user");
                        var sequenceBeforeReplay=new SqlEvidenceRepository(db).ReadStamps(access,[access.MemoryNamespace])[access.MemoryNamespace].CommitSequence;
                        await runtime.Knowledge.Context.IngestCommittedUserTurnAsync!(correctedSource.Id,default);
                        Check(mode+"_correction_replay_does_not_replan_or_write",runtime.Knowledge.LastIngestionStatus=="already-reviewed" && new SqlEvidenceRepository(db).ReadStamps(access,[access.MemoryNamespace])[access.MemoryNamespace].CommitSequence==sequenceBeforeReplay);
                        new SqlEvidenceRepository(db).Put(access.MemoryNamespace,new("unrelated","无关合成内容","user","unrelated","turn"));
                        new KnowledgeDeletionService(db).Forget(access,access.MemoryNamespace,["unrelated"]);
                        var afterEpoch=new SqlEvidenceRepository(db).ReadStamps(access,[access.MemoryNamespace])[access.MemoryNamespace].CommitSequence;
                        await runtime.Knowledge.Context.IngestCommittedUserTurnAsync!(correctedSource.Id,default);
                        Check(mode+"_privacy_epoch_change_does_not_republish_source",runtime.Knowledge.LastIngestionStatus=="already-reviewed" && new SqlEvidenceRepository(db).ReadStamps(access,[access.MemoryNamespace])[access.MemoryNamespace].CommitSequence==afterEpoch);
                        new KnowledgeDeletionService(db).Forget(access,access.MemoryNamespace,[correctedSource.Id]);
                        Check(mode+"_forgotten_correction_does_not_revive_old_fact",new HuTao.Knowledge.Facts.SqlAssertionRepository(db).Query(access,access.MemoryNamespace,new("user","drinks")).All(f=>f.Literal=="茶"));
                        new KnowledgeDeletionService(db).Forget(access,access.MemoryNamespace,[userSource.Id]);
                        Check(mode+"_learned_fact_removed_with_turn",new HuTao.Knowledge.Facts.SqlAssertionRepository(db).Query(access,access.MemoryNamespace,new("user","drinks")).All(f=>f.Literal=="茶"));
                    }
                    else
                    {
                        await runtime.Agent.GenerateSpeechSegmentsAsync("你好",false);
                        Check(mode+"_legacy_single_write_authority",runtime.MemoryStore!.Count==2 && new SqlConversationMemoryRepository(db,access).Load().State.Records.Count==0);
                        if(mode==KnowledgeRuntimeMode.Shadow)Check("shadow_retrieval_trace_without_second_reply",runtime.Knowledge!.LastShadowTrace.Any(x=>x.StartsWith("shadow-evidence-count=")) && runtime.Agent.History.Count(h=>h.Role=="assistant")==1);
                    }
                    callCounts[mode]=llm.Calls;
                }
                if(sql)
                {
                    var rejected=false;try{new AgentRuntimeFactory(directory,knowledgeOptions:options with {Mode=KnowledgeRuntimeMode.Legacy},llmFactory:_=>llm).Create(definition);}
                    catch(InvalidOperationException){rejected=true;}
                    Check(mode+"_cannot_silently_revert_to_json",rejected);
                    File.WriteAllText(Path.Combine(directory,"data","items.json"),"[]");
                    await using(var restarted=factory.Create(definition))
                        Check(mode+"_restart_ignores_retired_json_source",restarted.Agent.PrivacyStamp?.Epoch>0);
                    var boundMarker=Path.Combine(directory,"data","knowledge","authority-local-fixture-desktop.json");
                    using(var markerJson=System.Text.Json.JsonDocument.Parse(File.ReadAllText(boundMarker)))
                        Check(mode+"_authority_binds_database_lineage",markerJson.RootElement.GetProperty("lineageId").GetString()==KnowledgeRecovery.ReadLineage(db));
                    File.WriteAllText(boundMarker,System.Text.Json.JsonSerializer.Serialize(new {database=db.FilePath,space=access.MemoryNamespace,lineageId=new string('0',32)}));
                    var differentLineageRejected=false;
                    try{await using var wrong=factory.Create(definition);}catch(InvalidOperationException){differentLineageRejected=true;}
                    Check(mode+"_same_path_wrong_lineage_rejected",differentLineageRejected);
                }
                else
                {
                    var rejected=false;
                    try{new AgentRuntimeFactory(directory,knowledgeOptions:options with {Mode=KnowledgeRuntimeMode.Sql},llmFactory:_=>llm).Create(definition);}
                    catch(InvalidOperationException){rejected=true;}
                    Check(mode+"_json_migration_is_explicit",rejected);
                }
            }
            Check("shadow_does_not_call_second_model",callCounts[KnowledgeRuntimeMode.Legacy]==callCounts[KnowledgeRuntimeMode.Shadow]);
            foreach(var scenario in new[]{0,1,2})
            {
                var stale=scenario!=0;
                var migrationRoot=Path.Combine(root,"migration-"+scenario);Directory.CreateDirectory(Path.Combine(migrationRoot,"persona"));
                File.WriteAllText(Path.Combine(migrationRoot,"persona","system-prompt.md"),"你是测试角色。");
                var migrationDefinition=new CharacterDefinition("fixture","测试角色","","","persona","","","missing.json","你好","","忙碌",ImportantMemoryFile:"data/items.json",Tools:["document_reader"]);
                var migrationDb=new SqliteKnowledgeDatabase(Path.Combine(migrationRoot,"knowledge.db"));
                var migrationAccess=KnowledgeAccessContext.ForSession("local","fixture","desktop",[]);
                var jsonPath=Path.Combine(migrationRoot,"data","memory_fixture.json");
                var legacyStore=new ConversationMemoryStore(jsonPath);
                legacyStore.ObserveTurn("user","迁移前的合成对话",DateTimeOffset.Now);legacyStore.Flush();
                new HuTao.Knowledge.Import.MemoryJsonImporter(new SqlConversationMemoryRepository(migrationDb,migrationAccess)).Import(jsonPath,false);
                var item=new ImportantItem(Guid.NewGuid(),"合成提醒",DateTimeOffset.Now,null,null);
                var itemRepo=new SqlImportantItemRepository(migrationDb,migrationAccess);itemRepo.Load();itemRepo.Save([item]);
                File.WriteAllText(Path.Combine(migrationRoot,"data","items.json"),System.Text.Json.JsonSerializer.Serialize(new[]{scenario==2?item with {Content="迁移后改变的提醒"}:item}));
                if(scenario==1){legacyStore.ObserveTurn("user","迁移后的新增对话",DateTimeOffset.Now.AddMinutes(1));legacyStore.Flush();}
                var migrationFactory=new AgentRuntimeFactory(migrationRoot,knowledgeOptions:new(KnowledgeRuntimeMode.Sql,migrationDb.FilePath),llmFactory:_=>new FixtureLlm(true));
                var rejected=false;
                try{await using var migrated=migrationFactory.Create(migrationDefinition);}catch(InvalidOperationException){rejected=true;}
                Check(stale?"stale_migration_cannot_activate_"+scenario:"matching_migration_can_activate",rejected==stale && File.Exists(Path.Combine(migrationRoot,"data","knowledge","authority-local-fixture-desktop.json"))!=stale);
            }
            Environment.SetEnvironmentVariable("HU_TAO_MEMORY","false");
            var takeoverRoot=Path.Combine(root,"takeover");Directory.CreateDirectory(Path.Combine(takeoverRoot,"persona"));
            File.WriteAllText(Path.Combine(takeoverRoot,"persona","system-prompt.md"),"你是测试角色。");
            var takeoverDefinition=new CharacterDefinition("fixture","测试角色","","","persona","","","missing.json","你好","","忙碌",Tools:["document_reader"]);
            var takeoverLlm=new FixtureLlm(false);
            var takeoverFactory=new AgentRuntimeFactory(takeoverRoot,knowledgeOptions:new(KnowledgeRuntimeMode.Legacy,Path.Combine(takeoverRoot,"unused.db")),llmFactory:_=>takeoverLlm);
            await using(var runtime=takeoverFactory.Create(takeoverDefinition))
            {
                var segments=await runtime.Agent.GenerateSpeechSegmentsAsync("你好",false);
                var marker=Path.Combine(takeoverRoot,"data","knowledge","authority-local-fixture-desktop.json");
                takeoverLlm.OnCall=()=>File.WriteAllText(marker,"{}");
                var result=await ((ReactAgent)runtime.Agent).GenerateTextAsync("再聊聊",false);
                var blockedSegment=false;try{segments[0].DemandDelivery();}catch(OperationCanceledException){blockedSegment=true;}
                var blockedSpeech=false;try{await runtime.Agent.SynthesizeAsync("旧回复");}catch(OperationCanceledException){blockedSpeech=true;}
                Check("takeover_mid_generation_without_memory_blocks_reply_and_audio",result.Observation=="knowledge-changed" && runtime.Agent.History.Count==0 && blockedSegment && blockedSpeech);
                var calls=takeoverLlm.Calls;
                await runtime.Agent.GenerateSpeechSegmentsAsync("接管后",false);
                Check("deposed_agent_does_not_call_model",takeoverLlm.Calls==calls);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("HU_TAO_MEMORY",oldMemory);Environment.SetEnvironmentVariable("HU_TAO_IMMERSION",oldGate);
            Directory.Delete(root,true);
        }
        return results;
    }
    public class DisposalDriverProxy:System.Reflection.DispatchProxy
    {
        public int Calls;
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            if(method?.Name!="DisposeAsync")throw new InvalidOperationException("Unexpected driver operation in disposal fixture.");
            Calls++;
            if(Calls==1)throw new IOException("Synthetic driver disposal failure.");
            return method.ReturnType==typeof(ValueTask)?(object)ValueTask.CompletedTask:Task.CompletedTask;
        }
    }
    private sealed class CleanupFixture:IAsyncDisposable
    {
        public bool Fail=true;
        public int Calls;
        public ValueTask DisposeAsync()
        {
            Calls++;
            if(Fail)throw new IOException("Synthetic cleanup failure.");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedConsolidationLlm(string source):ILLMProvider
    {
        public string Name=>"delayed-consolidation-fixture";
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> CompleteAsync(string prompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)
        {
            Started.TrySetResult();await Release.Task.WaitAsync(ct);
            return System.Text.Json.JsonSerializer.Serialize(new {facts=new[]{new{text="用户喜欢温水",kind="preference",importance=.8,sources=new[]{source}}}});
        }
    }

    private sealed class FixtureLlm(bool knowledge) : ILLMProvider
    {
        public string Name=>"runtime-fixture";public int Calls {get;private set;}
        public Action? OnCall {get;set;}
        public string? FactSourceId {get;set;}
        public Task<string> CompleteAsync(string systemPrompt,IReadOnlyList<ChatMessage> history,CancellationToken ct=default)
        {
            Calls++;ct.ThrowIfCancellationRequested();
            OnCall?.Invoke();
            if(FactSourceId is not null && systemPrompt.Contains("Conversation Recall Planner"))return Task.FromResult("{\"tasks\":[{\"id\":\"m\",\"source\":\"memory\",\"strategy\":\"SqlFacts\",\"query\":\"晚上喝什么\",\"entity_bindings\":[{\"mention\":\"我\"}],\"predicates\":[\"drinks\"]}]}");
            if(systemPrompt.Contains("Conversation Recall Planner"))return Task.FromResult(knowledge?
                "{\"tasks\":[{\"id\":\"m\",\"source\":\"memory\",\"strategy\":\"SqlLexical\",\"query\":\"晚上 温水\"}]}":"{\"tasks\":[]}");
            if(systemPrompt.Contains("Evidence Support Checker"))return Task.FromResult("{\"segments\":[{\"index\":0,\"support\":\"supported\"}]}");
            if(systemPrompt.Contains("knowledge_references"))return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new {answerability="supported",segments=new[]{new{text="你晚上只喝温水。",kind="fact",evidence_ids=new[]{"memory:"+(FactSourceId??"source")}}}}));
            return Task.FromResult("你好呀。");
        }
    }
}
