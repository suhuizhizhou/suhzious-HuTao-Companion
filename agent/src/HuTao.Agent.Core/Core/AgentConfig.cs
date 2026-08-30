namespace HuTao.Agent.Core.Core;

/// <summary>Agent 运行配置。集中管理所有可切换/可调的选项。</summary>
public sealed class AgentConfig
{
    /// <summary>人设包目录（含 system-prompt.md、catchphrases.json）。</summary>
    public string PersonaRoot { get; init; } = "data/persona";

    /// <summary>选择的 TTS 引擎名（对应 ITtsEngine.Name），可切换。</summary>
    public string TtsEngine { get; init; } = "gpt-sovits";

    /// <summary>选择的 LLM 提供方名（对应 ILLMProvider.Name），可切换。</summary>
    public string LlmProvider { get; init; } = "mock";

    /// <summary>few-shot 参考音频路径（TTS 音色克隆用）。</summary>
    public string? TtsRefAudio { get; init; }

    /// <summary>few-shot 参考音频对应文本。</summary>
    public string? TtsRefText { get; init; }

    /// <summary>主动搭话的心跳间隔（秒）。</summary>
    public int ProactiveIntervalSeconds { get; init; } = 300;

    /// <summary>两次主动搭话之间的最小冷却（秒），防止频繁打扰。</summary>
    public int CooldownSeconds { get; init; } = 60;

    /// <summary>开启勿扰模式（用户忙碌时保持安静）。</summary>
    public bool DoNotDisturb { get; init; }
}
