using System.IO;
using System.Windows;
using HuTao.Dialogue.Core;
using HuTao.Dialogue.Runtime;
using HuTao.Dialogue.Sessions;
using HuTao.Foundation.Abstractions;
using HuTao.Persona;
using HuTao.Pet;
using HuTao.Foundation;
using HuTao.Dialogue.Llm;

/// <summary>真实 XAML/会话存储/Dispatcher；只有 Agent 是可延迟的替身。绝不装载 .env、GPU 或生产会话。</summary>
internal static class InteractiveUiFixture
{
    public static int Run(bool checks = false, bool live = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "hutao-interactive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("UI fixture root: " + root);
        var repo = ProjectEnvironment.FindRepositoryRoot(Environment.CurrentDirectory, AppContext.BaseDirectory);
        BudgetedProvider? provider = null;
        if (live)
        {
            if (repo is null) throw new InvalidOperationException("Repository root required.");
            ProjectEnvironment.LoadDotEnv([Path.Combine(repo, ".env")]);
            var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("DEEPSEEK_API_KEY required; no calls made.");
            provider = new BudgetedProvider(new DeepSeekLlmProvider(key, transportRetries: 1, maxOutputTokens: 350));
            Console.WriteLine("LIVE: DeepSeek, at most 6 requests; no TTS, no awareness tools, no production history.");
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/HuTao.Pet;component/ChatShellStyle.xaml", UriKind.Relative)
        });
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                var store = new SessionStore(root);
                var at = DateTimeOffset.Now;
                var descriptors = new List<SessionDescriptor>();
                foreach (var (id, name, color) in new[] { ("hutao", "胡桃", "#FFE08A3C"), ("furina", "芙宁娜", "#FF548BE0"), ("klee", "可莉", "#FFDC6060") })
                {
                    var descriptor = SessionFactory.Normalize(new SessionDescriptor(
                        "single_" + id, SessionKind.Single, name, name[..1], color,
                        [id], [new MemberPresence(id, at)], at, at, "UI 隔离测试", false, false, false, 0));
                    await store.CreateSessionAsync(descriptor);
                    descriptors.Add(descriptor);
                }
                await store.SaveCatalogAsync(new SessionCatalog(descriptors));
                var window = new ChatShellWindow(null, null, root)
                {
                    IsTestWindow = true, Width = 960, Height = 640,
                    WindowState = WindowState.Normal, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Title = "UI 验收 · 隔离数据"
                };
                window.Closed += (_, _) => Console.WriteLine($"UI fixture finished; live requests={provider?.Calls ?? 0}; data={root}");
                window.Show();
                await window.LoadSidebarOnlyAsync((descriptor, ct) =>
                {
                    var pool = new AgentPool((scope, character, token) =>
                    {
                        var persona = live ? PersonaLoader.Load(Path.Combine(repo!, character.PersonaDirectory))
                            : new PersonaProfile { Root = root, Name = character.Name, SystemPrompt = character.Name,
                                Catchphrases = new(), Lore = "", Quotes = [] };
                        IAgentConversation agent = live ? new ReactAgent(persona, provider!, null, [])
                            : new DelayedConversation(character.Name);
                        return Task.FromResult(new AgentRuntime(character, persona, agent, null!, null, null!, null!, []));
                    }, null);
                    return Task.FromResult(new ChatSessionRuntime(store, pool, descriptor, new SessionRuntimeOptions()));
                });
                if (checks)
                {
                    await UiInteractionChecks.RunAsync(window);
                    await window.RequestCloseAsync();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                app.Shutdown(1);
            }
        }));
        // 保留本次夹具方便复查；输出路径明确，不修改也不删除用户资料。
        return app.Run();
    }

    private sealed class BudgetedProvider(DeepSeekLlmProvider inner) : ILLMProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Name => inner.Name;
        public async Task<string> CompleteAsync(string prompt, IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call > 6) throw new LlmUnavailableException("UI live request budget exhausted.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = await inner.CompleteWithUsageAsync(prompt, history, deadline.Token);
            Console.WriteLine($"LIVE call={call} elapsed_ms={clock.ElapsedMilliseconds} prompt_tokens={result.PromptTokens} completion_tokens={result.CompletionTokens}");
            return result.Text;
        }
    }

    private sealed class DelayedConversation(string name) : IAgentConversation
    {
        private readonly List<ChatMessage> _history = [];
        public IReadOnlyList<ChatMessage> History => _history.ToArray();
        public void RestoreHistory(IEnumerable<ChatMessage> history) { _history.Clear(); _history.AddRange(history); }
        public ConversationContext? CaptureContext() => new(History, null);
        public async Task<AgentTurnResult> RespondAsync(string userInput, CancellationToken ct = default)
        {
            await Task.Delay(4000, ct);
            var reply = $"{name}收到啦，慢慢说，我在听。";
            _history.Add(new("user", userInput));
            _history.Add(new("assistant", reply));
            return new("", "", reply, null, "");
        }
        public Task<AgentTurnResult> ProactiveAsync(CancellationToken ct = default) => RespondAsync("", ct);
        public Task<IReadOnlyList<SpeechSegment>> GenerateSpeechSegmentsAsync(string? userInput, bool isProactive, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SpeechSegment>>([]);
        public Task<TtsResult?> SynthesizeAsync(string text, string emotion = "neutral", double intensity = 0.5, CancellationToken ct = default)
            => Task.FromResult<TtsResult?>(null);
    }
}
