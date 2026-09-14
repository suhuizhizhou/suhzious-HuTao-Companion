using System.Net.Http.Json;

namespace HuTao.Knowledge.Rag;

public sealed record StorySemanticHit(string Id, double Score);
public interface IStorySemanticSearch
{
    Task<IReadOnlyList<StorySemanticHit>> SearchAsync(string query, string corpusVersion, int topK, CancellationToken ct);
}

/// <summary>可选的本机 Dense 服务。模型、维度和语料版本由服务校验，不向公网发送对话。</summary>
public sealed class LocalStorySemanticSearch : IStorySemanticSearch, IDisposable
{
    private readonly HttpClient _client;
    public LocalStorySemanticSearch(Uri endpoint)
    {
        if (!endpoint.IsLoopback || endpoint.Scheme != "http")
            throw new ArgumentException("剧情语义服务仅支持本机 http 地址。", nameof(endpoint));
        _client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMilliseconds(650),
            MaxResponseContentBufferSize = 128 * 1024
        };
    }
    public async Task<IReadOnlyList<StorySemanticHit>> SearchAsync(string query, string corpusVersion, int topK, CancellationToken ct)
    {
        // 预先缓冲，明确 Content-Length；本地轻量服务不接受无界 chunked 请求体。
        using var payload = new StringContent(System.Text.Json.JsonSerializer.Serialize(
            new { query, corpus_version = corpusVersion, top_k = topK }), System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("search", payload, ct);
        response.EnsureSuccessStatusCode();
        var hits = await response.Content.ReadFromJsonAsync<List<StorySemanticHit>>(cancellationToken: ct) ?? [];
        return hits.Where(h => h is not null && !string.IsNullOrWhiteSpace(h.Id) && double.IsFinite(h.Score) && h.Score is >= -1 and <= 1)
            .DistinctBy(h => h.Id).Take(Math.Clamp(topK, 1, 100)).ToArray();
    }
    public void Dispose() => _client.Dispose();
}
