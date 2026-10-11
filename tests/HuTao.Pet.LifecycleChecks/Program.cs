using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using HuTao.Pet;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--interactive"))
            return InteractiveUiFixture.Run();
        if (args.Contains("--interaction-checks"))
            return InteractiveUiFixture.Run(checks: true);
        if (args.Contains("--interactive-live"))
            return InteractiveUiFixture.Run(live: true);
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/HuTao.Pet;component/ChatShellStyle.xaml", UriKind.Relative)
        });
        var failures=0;
        void Check(string name,bool passed)
        {Console.WriteLine($"{(passed?"PASS":"FAIL")} {name}");if(!passed)failures++;}
        app.Dispatcher.BeginInvoke(new Action(async ()=>
        {
            try
            {
                // No setup button is invoked: factory is deliberately unavailable.
                var window=new ChatShellWindow(null,null) {IsTestWindow=true,ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                var closed=false;window.Closed+=(_,_)=>closed=true;
                window.Show();
                var barrier=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                typeof(ChatShellWindow).GetField("_startup",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,barrier.Task);
                var closing=window.RequestCloseAsync();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check("real_window_close_waits_for_startup",!closed && !closing.IsCompleted);
                var duplicate=window.RequestCloseAsync();
                Check("duplicate_close_shares_completion",ReferenceEquals(closing,duplicate));
                barrier.SetResult();
                Check("real_window_closes_after_startup",await closing.WaitAsync(TimeSpan.FromSeconds(5)) && closed);
                var retryWindow=new ChatShellWindow(null,null) {IsTestWindow=true,ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                var retryClosed=false;retryWindow.Closed+=(_,_)=>retryClosed=true;retryWindow.Show();
                var pending=(HuTao.Dialogue.Runtime.PendingRuntimeCleanup)typeof(ChatShellWindow)
                    .GetField("_pendingCleanup",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(retryWindow)!;
                var owner=new FailingOwner();
                try {await pending.RetireAsync(owner);}catch(System.IO.IOException){}
                Check("cleanup_failure_keeps_real_window_open",!await retryWindow.RequestCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)) && !retryClosed);
                owner.Fail=false;
                Check("cleanup_retry_closes_real_window",await retryWindow.RequestCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)) && retryClosed);
                var drainingWindow=new ChatShellWindow(null,null) {IsTestWindow=true,ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                drainingWindow.Show();
                var turnBarrier=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                typeof(ChatShellWindow).GetField("_activeTurn",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(drainingWindow,turnBarrier.Task);
                var audioGate=(SemaphoreSlim)typeof(ChatShellWindow).GetField("_audioGate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(drainingWindow)!;
                await audioGate.WaitAsync();
                var draining=drainingWindow.RequestCloseAsync();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check("close_waits_for_active_turn",!draining.IsCompleted);
                turnBarrier.SetResult();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check("close_also_waits_for_independent_playback",!draining.IsCompleted);
                audioGate.Release();
                Check("close_completes_after_turn_and_playback",await draining.WaitAsync(TimeSpan.FromSeconds(5)));
                var faultWindow=new ChatShellWindow(null,null) {IsTestWindow=true,ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                faultWindow.Show();
                typeof(ChatShellWindow).GetField("_activeTurn",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(faultWindow,Task.FromException(new InvalidOperationException("Synthetic completed turn failure.")));
                Check("completed_turn_fault_does_not_block_cleanup",await faultWindow.RequestCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                var fileRoot=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hutao-wpf-flush-"+Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(fileRoot);
                try
                {
                    var filePath=System.IO.Path.Combine(fileRoot,"memory.json");
                    var store=new HuTao.Knowledge.Memory.ConversationMemoryStore(filePath);
                     store.ObserveTurn("user","V1",DateTimeOffset.Now);
                    store.Flush(throwOnError:true);
                     store.ObserveTurn("user","V2",DateTimeOffset.Now.AddMilliseconds(1));
                    var fileWindow=new ChatShellWindow(null,null) {IsTestWindow=true,ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                    fileWindow.Show();var fileClosed=false;fileWindow.Closed+=(_,_)=>fileClosed=true;
                    var fileQueue=(HuTao.Dialogue.Runtime.PendingRuntimeCleanup)typeof(ChatShellWindow).GetField("_pendingCleanup",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(fileWindow)!;
                    using(var lockedFile=new System.IO.FileStream(filePath,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.None))
                    {
                        try {await fileQueue.RetireAsync(new StoreOwner(store));}
                        catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException){}
                        Check("real_file_lock_blocks_window_close",!await fileWindow.RequestCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)) && !fileClosed);
                    }
                    Check("unlock_allows_window_close_retry",await fileWindow.RequestCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)) && fileClosed);
                    var restored=new HuTao.Knowledge.Memory.ConversationMemoryStore(filePath);
                     Check("window_retry_persists_pending_memory",restored.Count==2 && restored.Snapshot().Any(r=>r.Text=="V2"));
                }
                finally {System.IO.Directory.Delete(fileRoot,true);}
                // Verify actual WPF ownership semantics independently of MainWindow's services.
                var parent=new Window {Width=100,Height=100,ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                parent.Show();
                var owned=new ChatShellWindow(null,null) {IsTestWindow=true,Owner=parent,ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                owned.Show();
                var parentClosed=false;var ownedClosed=false;
                parent.Closed+=(_,_)=>parentClosed=true;owned.Closed+=(_,_)=>ownedClosed=true;
                var ownedQueue=(HuTao.Dialogue.Runtime.PendingRuntimeCleanup)typeof(ChatShellWindow).GetField("_pendingCleanup",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(owned)!;
                var ownedFailure=new FailingOwner();
                try {await ownedQueue.RetireAsync(ownedFailure);}catch(System.IO.IOException){}
                var ready=false;
                var closeAttempt=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                parent.Closing+=async (_,e)=>
                {
                    if(ready)return;
                    e.Cancel=true;
                    if(!await owned.RequestCloseAsync()){closeAttempt.TrySetResult(false);return;}
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    ready=true;parent.Close();closeAttempt.TrySetResult(true);
                };
                parent.Close();
                Check("owned_window_failure_preserves_parent",!await closeAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5)) && !parentClosed && !ownedClosed);
                ownedFailure.Fail=false;
                closeAttempt=new(TaskCreationOptions.RunContinuationsAsynchronously);
                parent.Close();
                Check("owned_window_retry_closes_child_then_parent",await closeAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5)) && parentClosed && ownedClosed);
                var initialized=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var initRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var preferencesSaved=0;
                var main=new MainWindow(()=>{initialized.TrySetResult();return initRelease.Task;},()=>preferencesSaved++)
                    {ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                var mainClosed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                main.Closed+=(_,_)=>mainClosed.TrySetResult();main.Show();
                await initialized.Task.WaitAsync(TimeSpan.FromSeconds(5));
                main.Close();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check("real_main_waits_for_initialization",!mainClosed.Task.IsCompleted && preferencesSaved==0);
                initRelease.SetResult();await mainClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check("real_main_closes_after_initialization",preferencesSaved==1);
                var faultMain=new MainWindow(()=>Task.FromException(new InvalidOperationException("Synthetic initialization fault.")),()=>{})
                    {ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                var faultMainClosed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                faultMain.Closed+=(_,_)=>faultMainClosed.TrySetResult();faultMain.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                faultMain.Close();await faultMainClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check("real_main_initialization_failure_still_closes",true);
                var integratedMain=new MainWindow(()=>Task.CompletedTask,()=>{})
                    {ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                integratedMain.Show();
                var integratedChild=new ChatShellWindow(null,null) {IsTestWindow=true,
                    Owner=integratedMain,ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                typeof(MainWindow).GetField("_chatRoom",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(integratedMain,integratedChild);
                integratedChild.Show();
                var closingOrder=new List<string>();
                var integratedClosed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                integratedChild.Closed+=(_,_)=>closingOrder.Add("child");
                integratedMain.Closed+=(_,_)=>{closingOrder.Add("main");integratedClosed.TrySetResult();};
                var integratedQueue=(HuTao.Dialogue.Runtime.PendingRuntimeCleanup)typeof(ChatShellWindow).GetField("_pendingCleanup",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(integratedChild)!;
                var integratedFailure=new FailingOwner();
                try {await integratedQueue.RetireAsync(integratedFailure);}catch(System.IO.IOException){}
                integratedMain.Close();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check("real_main_preserves_failed_child",closingOrder.Count==0 && !integratedClosed.Task.IsCompleted);
                integratedFailure.Fail=false;integratedMain.Close();
                await integratedClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check("real_main_waits_for_child_closed",closingOrder.SequenceEqual(new[]{"child","main"}));
                foreach(var taskField in new[]{"_characterSwitchTask","_userTurn","_documentTask","_backgroundTasks"})
                {
                    var taskMain=new MainWindow(()=>Task.CompletedTask,()=>{})
                        {ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000};
                    var taskClosed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    taskMain.Closed+=(_,_)=>taskClosed.TrySetResult();taskMain.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var operation=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    typeof(MainWindow).GetField(taskField,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(taskMain,
                        taskField=="_backgroundTasks"?new[]{operation.Task}:(object)operation.Task);
                    taskMain.Close();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check("main_waits_"+taskField,!taskClosed.Task.IsCompleted);
                    operation.SetResult();await taskClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Check("main_drains_"+taskField,true);
                }
                await RunSessionViewModelChecks(Check);
                await RunWindowRenderChecks(Check);
            }
            catch(Exception ex){Console.WriteLine("FAIL "+ex);failures++;}
            finally {app.Shutdown();}
        }));
        app.Run();
        return failures==0?0:1;
    }

    // -- QQNT session-switch end-to-end checks ------------------------------------
    // No GPU, no network, temp dir only.
    //
    // This runs the REAL ChatShellViewModel + SessionStore + ChatSessionRuntime +
    // AgentPool; only the outermost layer (assembling an AgentRuntime, which needs
    // persona/voice/knowledge files) is stubbed. That is exactly what makes
    // "does switching sessions leak or lose history" observable.
    private static async Task RunSessionViewModelChecks(Action<string,bool> Check)
    {
        var root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hutao-vm-"+Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var store=new HuTao.Dialogue.Sessions.SessionStore(root);
            var catalog=new HuTao.Dialogue.Sessions.SessionCatalog([]);
            var at=DateTimeOffset.Now.AddSeconds(-1);
            var single=HuTao.Dialogue.Sessions.SessionFactory.Normalize(
                new HuTao.Dialogue.Sessions.SessionDescriptor(
                    "single_hutao",HuTao.Dialogue.Sessions.SessionKind.Single,"Hutao","H","#FFE08A3C",
                    ["hutao"],[new HuTao.Dialogue.Sessions.MemberPresence("hutao",at)],
                    at,at,"",false,false,false,0));
            var group=HuTao.Dialogue.Sessions.SessionFactory.Normalize(
                new HuTao.Dialogue.Sessions.SessionDescriptor(
                    "group_default",HuTao.Dialogue.Sessions.SessionKind.Group,"TeaParty","T","#FF8C60BE",
                    ["hutao","lacrimosa"],
                    [new HuTao.Dialogue.Sessions.MemberPresence("hutao",at),
                     new HuTao.Dialogue.Sessions.MemberPresence("lacrimosa",at)],
                    at,at,"",true,false,false,0));
            await store.CreateSessionAsync(single);
            await store.CreateSessionAsync(group);
            catalog=catalog.Upsert(single).Upsert(group);
            await store.SaveCatalogAsync(catalog);

            var vm=new HuTao.Pet.ChatShell.ChatShellViewModel(store,catalog);
            // One pool per session, mirroring the window. The stub returns a fake
            // agent whose history is writable, so context handoff is observable.
            vm.UseRuntimeFactory((descriptor,ct)=>
            {
                var pool=new HuTao.Dialogue.Runtime.AgentPool(
                    (scope,definition,_)=>Task.FromResult(FakeRuntime(definition,new ScriptedAgent())),null);
                return Task.FromResult(new HuTao.Dialogue.Sessions.ChatSessionRuntime(
                    store,pool,descriptor,
                    new HuTao.Dialogue.Sessions.SessionRuntimeOptions{
                        Director=new FixedDirector(descriptor.MemberIds.FirstOrDefault()??"hutao"),
                        RoomOptions=new HuTao.Dialogue.ChatRoom.ChatRoomOptions{MinTurns=1,MaxTurns=4}}));
            });

            // Pass the real character catalog so display names resolve (search matches them).
            // Migration finds no legacy chat logs under this temp root, so it creates nothing.
            await vm.InitializeAsync(root,HuTao.Persona.CharacterCatalog.All);
            Check("vm_initializes_with_existing_sessions",vm.Sessions.Count==2);
            Check("vm_selects_a_session",vm.ActiveSessionId is not null);

            // Say something in the single chat: only its turns should appear.
            await vm.SwitchSessionAsync("single_hutao");
            var singleBefore=vm.ActiveTurns.Count;
            await vm.SendAsync("HELLO_SINGLE");
            Check("vm_send_appends_to_active_session",vm.ActiveTurns.Count>singleBefore);
            Check("vm_own_message_is_visible",
                vm.ActiveTurns.Any(t=>t.IsUser&&t.Text=="HELLO_SINGLE"));
            var singleCount=vm.ActiveTurns.Count;

            // Switch to the group: its turns must not include the single chat's.
            await vm.SwitchSessionAsync("group_default");
            Check("vm_switch_does_not_leak_other_session",
                vm.ActiveTurns.All(t=>t.Text!="HELLO_SINGLE")&&
                vm.ActiveTurns.All(t=>t.Text!="LINE_FROM_HUTAO"));
            var groupBefore=vm.ActiveTurns.Count;
            await vm.SendAsync("HELLO_GROUP");
            Check("vm_group_turn_recorded",vm.ActiveTurns.Count>groupBefore);
            var groupCount=vm.ActiveTurns.Count;

            // Switch back: the single chat's history must come back intact.
            await vm.SwitchSessionAsync("single_hutao");
            Check("vm_switch_back_restores_history",vm.ActiveTurns.Count==singleCount);

            // Unread accumulates only for inactive sessions.
            await vm.BumpUnreadForOthersAsync("group_default");
            var groupItem=vm.Sessions.First(s=>s.Id=="group_default");
            Check("vm_unread_bumps_inactive_session",groupItem.UnreadCount>0);
            await vm.BumpUnreadForOthersAsync("single_hutao");
            var singleItem=vm.Sessions.First(s=>s.Id=="single_hutao");
            Check("vm_unread_skips_active_session",singleItem.UnreadCount==0);

            // Pinning must reorder the sidebar (order comes from SessionCatalog.Ordered).
            await vm.SetPinnedAsync("group_default",true);
            Check("vm_pin_reorders_sidebar",vm.Sessions[0].Id=="group_default");

            // Search matches member display names too, not just the group title.
            vm.SearchText=HuTao.Persona.CharacterCatalog.Get("lacrimosa").Name;
            vm.RefreshSessions();
            Check("vm_search_matches_member",
                vm.Sessions.Count==1&&vm.Sessions[0].Id=="group_default");
            vm.SearchText="";
            vm.RefreshSessions();

            // Deleting the active session must fall back to the remaining one.
            await vm.DeleteAsync("single_hutao");
            Check("vm_delete_moves_to_remaining_session",
                vm.Sessions.Count==1&&vm.ActiveSessionId=="group_default");

            // -- regressions the REAL end-to-end run caught --------------------------
            // (1) The runtime raises DescriptorChanged on a THREAD POOL thread. Without a
            //     dispatcher hook the handler touched UI-bound collections and threw
            //     "only valid on the Dispatcher thread", which killed the turn:
            //     the message was persisted but the character never replied.
            //     Must run while a session is still active (a turn raises the notification).
            await vm.SwitchSessionAsync("single_hutao");
            var dispatched=0;
            var recorded=vm.Dispatch;
            vm.Dispatch=a=>{dispatched++;recorded?.Invoke(a);};
            await vm.StepAsync();
            Check("vm_runtime_notifications_go_through_dispatch",dispatched>0);
            Check("vm_dispatch_is_wired",true);
            vm.Dispatch=recorded;

            await vm.ShutdownAsync();
            Check("vm_shutdown_completes",true);

            // (2) A derived (getter-only) property serialized into index.json makes the
            //     whole catalog unreadable on the next start. The real run wrote
            //     "ActivePresences" and every subsequent load returned an EMPTY catalog.
            var raw=await System.IO.File.ReadAllTextAsync(
                System.IO.Path.Combine(root,"data","conversations","index.json"));
            Check("vm_index_has_no_derived_properties",
                !raw.Contains("ActivePresences",StringComparison.Ordinal));
            Check("vm_index_still_parses_after_writes",
                (await new HuTao.Dialogue.Sessions.SessionStore(root).LoadCatalogAsync()).Sessions.Count>0);
        }
        catch(Exception ex)
        {
            Console.WriteLine("FAIL vm_unexpected "+ex.GetType().Name+": "+ex.Message);
            Check("vm_unexpected",false);
        }
        finally
        {
            try{System.IO.Directory.Delete(root,true);}catch(System.IO.IOException){}
        }
    }

    // -- Window rendering smoke checks --------------------------------------------
    // No GPU, no network. Builds a QQNT window over a TEMP repository so the real
    // XAML (styles, row template, message bubbles, responsive collapse) is exercised
    // without touching the real repo or starting the TTS engine.
    private static async Task RunWindowRenderChecks(Action<string,bool> Check)
    {
        var root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"hutao-ui-"+Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        string? previous=Environment.GetEnvironmentVariable("HU_TAO_REPO_ROOT");
        try
        {
            var store=new HuTao.Dialogue.Sessions.SessionStore(root);
            var at=DateTimeOffset.Now.AddSeconds(-1);
            var single=HuTao.Dialogue.Sessions.SessionFactory.Normalize(
                new HuTao.Dialogue.Sessions.SessionDescriptor(
                    "single_hutao",HuTao.Dialogue.Sessions.SessionKind.Single,"Hutao","H","#FFE08A3C",
                    ["hutao"],[new HuTao.Dialogue.Sessions.MemberPresence("hutao",at)],
                    at,at,"",false,false,false,0));
            var group=HuTao.Dialogue.Sessions.SessionFactory.Normalize(
                new HuTao.Dialogue.Sessions.SessionDescriptor(
                    "group_default",HuTao.Dialogue.Sessions.SessionKind.Group,"TeaParty","T","#FF8C60BE",
                    ["hutao","lacrimosa"],
                    [new HuTao.Dialogue.Sessions.MemberPresence("hutao",at),
                     new HuTao.Dialogue.Sessions.MemberPresence("lacrimosa",at)],
                    at,at,"",true,false,false,3));
            await store.CreateSessionAsync(single);
            await store.CreateSessionAsync(group);
            var catalog=new HuTao.Dialogue.Sessions.SessionCatalog([single,group]);
            await store.SaveCatalogAsync(catalog);
            await store.AppendAsync("group_default",new HuTao.Dialogue.Sessions.ConversationTurn(
                "",at,"user","you","hello group",true,null,
                ["hutao","lacrimosa"],HuTao.Dialogue.Sessions.ConversationTurnKind.Message));
            await store.AppendAsync("group_default",new HuTao.Dialogue.Sessions.ConversationTurn(
                "",at.AddSeconds(1),"hutao","Hutao","hi there",false,null,
                ["lacrimosa"],HuTao.Dialogue.Sessions.ConversationTurnKind.Message));
            await store.AppendAsync("group_default",new HuTao.Dialogue.Sessions.ConversationTurn(
                "",at.AddSeconds(2),"system","","nobody could speak",false,null,
                [],HuTao.Dialogue.Sessions.ConversationTurnKind.System));

            Environment.SetEnvironmentVariable("HU_TAO_REPO_ROOT",root);
            // factory=null => the window skips agent assembly but still loads its sessions.
            var window=new HuTao.Pet.ChatShellWindow(null,null)
                {IsTestWindow=true,ShowInTaskbar=false,ShowActivated=false,Left=-30000,Top=-30000,Width=960,Height=640};
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            // Load the catalog without assembling agents, then let the rows realize.
            await window.LoadSidebarOnlyAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            for (var i=0;i<25 && window.SessionListForCheck.Items.Count==0;i++)
                await Task.Delay(40);

            Check("ui_window_loads_with_sessions",window.SessionListForCheck.Items.Count==2);
            Check("ui_sidebar_shows_unread_badge",
                HuTao.Pet.ChatShellWindow.FindVisual(window.SessionList,"BadgeHost")
                    is System.Windows.Controls.ContentControl { Content: not null });

            // Responsive collapse: narrow the window and confirm the sidebar column shrinks.
            window.Width=760;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
            Check("ui_sidebar_collapses_when_narrow",window.SidebarWidthForCheck<=80);

            Check("ui_request_close_completes",
                await window.RequestCloseAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            // The headless window must NOT write its off-screen (-30000,-30000) position
            // into the real user preference file. It did once, and the user's next launch
            // opened the chat window off-screen -- visually "clicking the button does nothing".
            var pref=System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HuTaoCompanion","chat-shell.json");
            if(System.IO.File.Exists(pref))
            {
                var saved=System.Text.Json.JsonSerializer.Deserialize<ChatShellPrefsProbe>(
                    await System.IO.File.ReadAllTextAsync(pref));
                Check("ui_headless_window_does_not_pollute_real_preferences",
                    saved is null || (saved.Left > -10000 && saved.Top > -10000));
            }
            else
            {
                Check("ui_headless_window_does_not_pollute_real_preferences",true);
            }
        }
        catch(Exception ex)
        {
            Console.WriteLine("FAIL ui_unexpected "+ex.GetType().Name+": "+ex.Message);
            Check("ui_unexpected",false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HU_TAO_REPO_ROOT",previous);
            try{System.IO.Directory.Delete(root,true);}catch(System.IO.IOException){}
        }
    }

    private static HuTao.Dialogue.Runtime.AgentRuntime FakeRuntime(        HuTao.Persona.CharacterDefinition definition,HuTao.Dialogue.Core.IAgentConversation agent)
        => new(definition,
            new HuTao.Persona.PersonaProfile{
                Root=System.IO.Path.GetTempPath(),Name=definition.Name,
                SystemPrompt="You are "+definition.Name,Catchphrases=new(),Lore="",Quotes=[]},
            agent,
            DocumentReader:null!,
            Tts:null,
            EmotionReferences:null!,
            OriginalVoices:null!,
            Tools:[]);

    /// <summary>Fake conversation with writable history; proves context handoff works.</summary>
    private sealed class ScriptedAgent:HuTao.Dialogue.Core.IAgentConversation
    {
        private readonly List<HuTao.Foundation.Abstractions.ChatMessage> _history=[];
        public IReadOnlyList<HuTao.Foundation.Abstractions.ChatMessage> History=>_history.ToArray();
        public HuTao.Dialogue.Core.ConversationPrivacyStamp? PrivacyStamp=>null;
        public void RestoreHistory(IEnumerable<HuTao.Foundation.Abstractions.ChatMessage> history)
        {_history.Clear();_history.AddRange(history);}
        public HuTao.Dialogue.Core.ConversationContext? CaptureContext()=>new(History,PrivacyStamp);
        public Task<HuTao.Dialogue.Core.AgentTurnResult> RespondAsync(
            string userInput,CancellationToken ct=default)
        {
            _history.Add(new HuTao.Foundation.Abstractions.ChatMessage("user",userInput));
            _history.Add(new HuTao.Foundation.Abstractions.ChatMessage("assistant","LINE_FROM_HUTAO"));
            return Task.FromResult(new HuTao.Dialogue.Core.AgentTurnResult("","","LINE_FROM_HUTAO",null,""));
        }
        public Task<HuTao.Dialogue.Core.AgentTurnResult> ProactiveAsync(CancellationToken ct=default)
        {
            _history.Add(new HuTao.Foundation.Abstractions.ChatMessage("assistant","LINE_FROM_HUTAO"));
            return Task.FromResult(new HuTao.Dialogue.Core.AgentTurnResult("","","LINE_FROM_HUTAO",null,""));
        }
        public Task<IReadOnlyList<HuTao.Dialogue.Core.SpeechSegment>> GenerateSpeechSegmentsAsync(
            string? userInput,bool isProactive,CancellationToken ct=default)
            =>Task.FromResult<IReadOnlyList<HuTao.Dialogue.Core.SpeechSegment>>([]);
        public Task<HuTao.Foundation.Abstractions.TtsResult?> SynthesizeAsync(
            string text,string emotion="neutral",double intensity=0.5,CancellationToken ct=default)
            =>Task.FromResult<HuTao.Foundation.Abstractions.TtsResult?>(null);
    }

    private sealed class FixedDirector(string speakerId):HuTao.Dialogue.ChatRoom.IChatRoomDirector
    {
        public Task<HuTao.Dialogue.ChatRoom.DirectorDecision> DecideAsync(
            IReadOnlyList<HuTao.Dialogue.ChatRoom.ChatRoomParticipant> participants,
            IReadOnlyList<HuTao.Dialogue.ChatRoom.ChatRoomTurn> transcript,
            string? userInput,CancellationToken ct=default)
            =>Task.FromResult(new HuTao.Dialogue.ChatRoom.DirectorDecision(speakerId,"test",false));
    }

    private sealed class StoreOwner(HuTao.Knowledge.Memory.ConversationMemoryStore store):IAsyncDisposable
    {
        public ValueTask DisposeAsync(){store.Flush(throwOnError:true);return ValueTask.CompletedTask;}
    }

    /// <summary>Mirrors the chat window's saved preferences so the check can read them back.</summary>
    private sealed record ChatShellPrefsProbe(double? Width,double? Height,double Left,double Top,bool Maximized);

    /// <summary>Fails on demand, to prove cleanup failures keep the window open.</summary>
    private sealed class FailingOwner:IAsyncDisposable    {
        public bool Fail=true;
        public ValueTask DisposeAsync()
        {
            if(Fail)throw new System.IO.IOException("Synthetic lifecycle cleanup failure.");
            return ValueTask.CompletedTask;
        }
    }
}
