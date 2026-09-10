using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HuTao.Agent.Core.Abstractions;

namespace HuTao.Agent.Core.Llm;

/// <summary>
/// DeepSeek 大模型提供方（OpenAI 兼容 chat/completions 接口）。
/// 通过环境变量 DEEPSEEK_API_KEY 提供密钥；模型默认 deepseek-chat。
/// 与 MockLlmProvider 并列，都实现 ILLMProvider，靠 Name 区分、按配置切换。
/// </summary>
public sealed class DeepSeekLlmProvider : ILLMProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly double _temperature;

    public DeepSeekLlmProvider(
        string apiKey,
        string model = "deepseek-chat",
        string baseUrl = "https://api.deepseek.com",
        double temperature = 0.9)
    {
        _apiKey = apiKey;
        _model = model;
        _temperature = temperature;
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    public string Name => "deepseek";

    public async Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default)
    {
        // 组装 OpenAI 兼容的 messages
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var m in history)
            messages.Add(new { role = m.Role, content = m.Content });

        var payload = new
        {
            model = _model,
            messages,
            temperature = _temperature,
            max_tokens = 1000,
            stream = false,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepSeek API 错误 {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content?.Trim() ?? "";
    }
}
