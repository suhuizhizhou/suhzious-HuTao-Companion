using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Llm;

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

    /// <summary>传输层重试次数。只重试「连接类」错误，4xx 这类确定性错误立刻抛出。</summary>
    private const int TransportRetries = 3;

    public async Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default)
    {
        // 组装 OpenAI 兼容的 messages
        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var m in history)
            messages.Add(new { role = m.Role, content = m.Content });

        var payloadJson = JsonSerializer.Serialize(new
        {
            model = _model,
            messages,
            temperature = _temperature,
            max_tokens = 1000,
            stream = false,
        });

        HttpResponseMessage? resp = null;
        try
        {
            // 长会话里偶发的连接重置是常态（实测见过 `SocketException 10054 远程主机强迫关闭`）。
            // 一次抖动就让整轮对话炸掉是不合理的，所以这里退避重试。
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, "/chat/completions");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    req.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                    resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < TransportRetries)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    throw new LlmUnavailableException(
                        $"DeepSeek 连接失败，已重试 {TransportRetries} 次：{ex.Message}", ex);
                }
            }

            using var owned = resp;
            var body = await owned!.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // 5xx / 429 也算「暂时不可用」：对端的问题，不是我们的请求有问题。
            if ((int)owned.StatusCode >= 500 || owned.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new LlmUnavailableException($"DeepSeek 暂时不可用 {(int)owned.StatusCode}: {body}");

            if (!owned.IsSuccessStatusCode)
                throw new InvalidOperationException($"DeepSeek API 错误 {(int)owned.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return content?.Trim() ?? "";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>连接类/超时类错误；HTTP 状态码错误不算（那些重试也没用）。</summary>
    private static bool IsTransient(Exception ex)
        => ex is HttpRequestException or IOException or System.Net.Sockets.SocketException
           || ex.InnerException is not null && IsTransient(ex.InnerException);
}
