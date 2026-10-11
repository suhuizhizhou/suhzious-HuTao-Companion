using HuTao.Dialogue.Core;
using HuTao.Dialogue.Storage;

internal static class ChatPrivacyChecks
{
    public static List<CheckResult> Run()
    {
        var checks=new List<CheckResult>();void Check(string name,bool ok)=>checks.Add(new("chat_privacy_"+name,ok,"isolated host log/audio"));
        var root=Path.Combine(Path.GetTempPath(),"hutao-chat-privacy-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var generated=Path.Combine(root,"generated");Directory.CreateDirectory(generated);
            var owned=Path.Combine(generated,"agent_tts_owned.wav");File.WriteAllText(owned,"owned fixture");
            var external=Path.Combine(root,"agent_tts_training.wav");File.WriteAllText(external,"external fixture");
            var reference=Path.Combine(generated,"reference.wav");File.WriteAllText(reference,"reference fixture");
            var unowned=Path.Combine(generated,"agent_tts_unowned.wav");File.WriteAllText(unowned,"unowned fixture");
            var path=Path.Combine(root,"chat.json");var stamp=new ConversationPrivacyStamp("memory:test:hutao:desktop",0);
            var store=new ChatLogStore(path,privacy:()=>stamp,generatedAudioRoot:generated);
            ChatEntry Entry(string text,string? audio=null,bool owns=false)=>new("now","assistant",text,audio) {Privacy=stamp,OwnsGeneratedAudio=owns};
            var entries=new[]{Entry("forgotten text",owned,true),Entry("external",external,true),Entry("reference",reference,true),Entry("unowned",unowned),new ChatEntry("old","user","unversioned",null)};
            store.Save(entries);Check("current_and_pre_migration_log_load",store.Load().Count==5);
            var pruning=new ChatLogStore(path,audioSizeLimitBytes:0,privacy:()=>stamp,generatedAudioRoot:generated);
            Check("quota_pruning_never_deletes_reference_or_external_audio",pruning.ComputePrunableAudio(entries.ToList()).SequenceEqual([owned]));
            stamp=stamp with {Epoch=1};var restarted=new ChatLogStore(path,privacy:()=>stamp,generatedAudioRoot:generated);
            Check("restart_removes_old_and_unversioned_log",restarted.Load().Count==0 && !File.ReadAllText(path).Contains("forgotten text"));
            Check("only_owned_generated_audio_deleted",!File.Exists(owned) && File.Exists(external) && File.Exists(reference) && File.Exists(unowned));
            store.Save(entries);Check("late_old_writer_cannot_restore",restarted.Load().Count==0);
            var current=Entry("new version reply");store.Save([current]);
            Check("new_epoch_persists",restarted.Load().Single().Privacy==stamp);
            var foreign=new ConversationPrivacyStamp("memory:test:hutao:chatroom",1);
            var otherStore=new ChatLogStore(path,privacy:()=>foreign,generatedAudioRoot:generated);
            Check("wrong_scope_cannot_read_or_erase",otherStore.Load().Count==0 && store.Load().Single().Text==current.Text);
            var rejected=false;try{otherStore.Save([current]);}catch(InvalidOperationException){rejected=true;}
            Check("wrong_scope_cannot_write",rejected && store.Load().Count==1);
            rejected=false;try{otherStore.Save([]);}catch(InvalidOperationException){rejected=true;}
            Check("wrong_scope_empty_save_cannot_erase",rejected && store.Load().Count==1);
            rejected=false;try{otherStore.Clear();}catch(InvalidOperationException){rejected=true;}
            Check("wrong_scope_clear_cannot_erase",rejected && store.Load().Count==1);
            var locked=Path.Combine(generated,"agent_tts_locked.wav");File.WriteAllText(locked,"locked fixture");
            store.Save([Entry("pending purge",locked,true)]);
            using(var handle=new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                stamp=stamp with {Epoch=2};
                Check("locked_audio_does_not_keep_old_text",store.Load().Count==0 && File.Exists(locked));
            }
            new ChatLogStore(path,privacy:()=>stamp,generatedAudioRoot:generated).Load();
            Check("locked_audio_purge_retries_after_restart",!File.Exists(locked));
            var legacy=new ChatLogStore(Path.Combine(root,"legacy.json"));legacy.Save([new("old","user","legacy",null)]);
            Check("legacy_json_roundtrip",legacy.Load().Single().Text=="legacy");
            var rollbackSource=Path.Combine(root,"rollback-source.json");
            var rollbackTarget=Path.Combine(root,"rollback-target.json");
            var conversionEntries=new[]{Entry("current retained",external),Entry("stale excluded",owned) with {Privacy=stamp with {Epoch=1}},new ChatEntry("old","user","unversioned excluded",null)};
            File.WriteAllText(rollbackSource,System.Text.Json.JsonSerializer.Serialize(conversionEntries),System.Text.Encoding.UTF8);
            var sourceBytes=File.ReadAllBytes(rollbackSource);
            var conversion=ChatLogRollback.Prepare(rollbackSource,rollbackTarget,stamp);
            var converted=new ChatLogStore(rollbackTarget).Load();
            Check("rollback_log_filters_privacy_and_preserves_current_audio",conversion.Retained==1 && conversion.Excluded==2 && converted.Single().Privacy is null && converted.Single().Audio==external && converted.Single().Text=="current retained" && File.Exists(external) && sourceBytes.SequenceEqual(File.ReadAllBytes(rollbackSource)));
            var targetBytes=File.ReadAllBytes(rollbackTarget);var overwriteRejected=false;
            try{ChatLogRollback.Prepare(rollbackSource,rollbackTarget,stamp);}catch(IOException){overwriteRejected=true;}
            Check("rollback_log_never_overwrites_existing_destination",overwriteRejected && targetBytes.SequenceEqual(File.ReadAllBytes(rollbackTarget)) && !Directory.EnumerateFiles(root,".chat-rollback-*").Any());
            File.AppendAllText(rollbackSource," ");var changedRejected=false;
            try{ChatLogRollback.Prepare(rollbackSource,Path.Combine(root,"changed-log.json"),stamp,conversion.SourceSha256);}catch(InvalidDataException){changedRejected=true;}
            Check("rollback_log_binds_reviewed_bytes",changedRejected && !File.Exists(Path.Combine(root,"changed-log.json")));
            foreach(var invalidStamp in new[]{stamp with {Epoch=stamp.Epoch+1},foreign})
            {
                File.WriteAllText(rollbackSource,System.Text.Json.JsonSerializer.Serialize(new[]{Entry("reject") with {Privacy=invalidStamp}}));
                var mismatch=false;try{ChatLogRollback.Prepare(rollbackSource,Path.Combine(root,"mismatched-log.json"),stamp);}catch(InvalidDataException){mismatch=true;}
                Check("rollback_log_rejects_authority_"+invalidStamp.Namespace+"_"+invalidStamp.Epoch,mismatch && !File.Exists(Path.Combine(root,"mismatched-log.json")));
            }
        }
        finally {Directory.Delete(root,true);}
        return checks;
    }
}
