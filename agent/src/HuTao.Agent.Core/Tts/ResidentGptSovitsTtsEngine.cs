using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using HuTao.Agent.Core.Abstractions;
using HuTao.Agent.Core.Runtime;

namespace HuTao.Agent.Core.Tts;

/// <summary>
/// 常驻 GPT-SoVITS HTTP 客户端。
/// 模型由一个隐藏的 Python 服务进程加载一次并常驻；本类通过串行门提交 GPU 推理，
/// 避免连续聊天气泡重复加载权重或并发抢占同一组模型状态。
/// </summary>
public sealed class ResidentGptSovitsTtsEngine : ITtsEngine, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _outputDir;
    private readonly string? _python;
    private readonly string? _serverScript;
    private readonly string? _gptModel;
    private readonly string? _sovitsModel;
    private readonly string? _pythonPath;
    private readonly string _device;
    private readonly bool _half;
    private readonly bool _autoStart;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private readonly CancellationTokenSource _lifecycleCts = new();
    private readonly object _backgroundStartLock = new();
    private Process? _serverProcess;
    private Task? _backgroundStart;
    private int _disposeStarted;

    public ResidentGptSovitsTtsEngine(
        Uri serverUrl,
        string outputDir,
        string? python = null,
        string? serverScript = null,
        string? gptModel = null,
        string? sovitsModel = null,
        string? pythonPath = null,
        string device = "cuda",
        bool half = true,
        bool autoStart = true,
        HttpClient? httpClient = null)
    {
        _outputDir = outputDir;
        _python = python;
        _serverScript = serverScript;
        _gptModel = gptModel;
        _sovitsModel = sovitsModel;
        _pythonPath = pythonPath;
        _device = device;
        _half = half;
        _autoStart = autoStart;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = serverUrl;
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    public string Name => "gpt-sovits-resident";

    /// <summary>在后台启动并等待模型加载，不阻塞 WPF 界面初始化。</summary>
    public void StartInBackground()
    {
        lock (_backgroundStartLock)
        {
            if (_backgroundStart is not null)
                return;

            _backgroundStart = Task.Run(async () =>
            {
                try
                {
                    await EnsureReadyAsync(_lifecycleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifecycleCts.IsCancellationRequested)
                {
                    // 桌宠关闭时取消后台启动。
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[resident-tts] 后台启动失败: {ex.Message}");
                }
            });
        }
    }

    /// <summary>确保服务可用；需要时以隐藏窗口启动 Python，并等待模型加载完成。</summary>
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0, this);

        if (await IsHealthyAsync(ct).ConfigureAwait(false))
            return;

        if (!_autoStart)
            throw new InvalidOperationException($"常驻 TTS 服务未运行: {_http.BaseAddress}");

        await _startupGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await IsHealthyAsync(ct).ConfigureAwait(false))
                return;

            StartServerProcess();

            var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (_serverProcess?.HasExited == true)
                    throw new InvalidOperationException(
                        $"常驻 TTS 服务启动失败，Python 退出码: {_serverProcess.ExitCode}");

                if (await IsHealthyAsync(ct).ConfigureAwait(false))
                    return;

                await Task.Delay(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
            }

            throw new TimeoutException("等待 GPT-SoVITS 常驻服务加载模型超时");
        }
        finally
        {
            _startupGate.Release();
        }
    }

    /// <summary>缓存参考音频特征，减少当前角色第一次合成时的额外等待。</summary>
    public async Task WarmupAsync(string referenceAudioPath, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        using var response = await _http.PostAsJsonAsync(
            "warmup", new { ref_audio_path = Path.GetFullPath(referenceAudioPath) }, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateHttpExceptionAsync("TTS 预热失败", response, ct).ConfigureAwait(false);
    }

    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);

        // GPT-SoVITS 的 prompt cache 属于模型级共享状态，因此一个服务实例只串行处理请求。
        await _inferenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payload = new
            {
                text = request.Text,
                text_lang = NormalizeLanguage(request.Language),
                ref_audio_path = Path.GetFullPath(
                    request.RefAudioPath ?? throw new ArgumentNullException(
                        nameof(request.RefAudioPath), "few-shot 需要参考音频")),
                prompt_text = request.RefText ?? "",
                prompt_lang = NormalizeLanguage(request.Language),
                top_k = 20,
                top_p = 0.6,
                temperature = Math.Clamp(request.Temperature, 0.4, 0.9),
                text_split_method = "cut5",
                batch_size = 1,
                speed_factor = Math.Clamp(request.SpeedFactor, 0.8, 1.15),
                fragment_interval = 0.3,
                seed = -1,
                media_type = "wav",
                streaming_mode = false,
                parallel_infer = true,
                sample_steps = 32,
                super_sampling = false,
            };

            using var response = await _http.PostAsJsonAsync("tts", payload, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await CreateHttpExceptionAsync("常驻 TTS 合成失败", response, ct).ConfigureAwait(false);

            Directory.CreateDirectory(_outputDir);
            var outPath = Path.Combine(_outputDir, $"agent_tts_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wav");
            await using (var output = new FileStream(
                             outPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                             bufferSize: 81920, useAsync: true))
            {
                await response.Content.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            var (sampleRate, durationSeconds) = ReadWaveMetadata(outPath);
            return new TtsResult(outPath, sampleRate, durationSeconds);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            using var response = await _http.GetAsync("health", timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private void StartServerProcess()
    {
        if (_serverProcess is { HasExited: false })
            return;

        foreach (var (value, label) in new[]
                 {
                     (_python, "Python"), (_serverScript, "常驻服务脚本"),
                     (_gptModel, "GPT 权重"), (_sovitsModel, "SoVITS 权重"),
                 })
        {
            if (string.IsNullOrWhiteSpace(value) || !File.Exists(value))
                throw new FileNotFoundException($"找不到 {label}: {value}");
        }

        if (!PortablePythonRuntime.TryPrepare(_python!, _pythonPath))
            throw new InvalidOperationException("便携 Python 初始化失败");

        var endpoint = _http.BaseAddress
                       ?? throw new InvalidOperationException("常驻 TTS URL 未配置");
        var psi = new ProcessStartInfo
        {
            FileName = _python!,
            WorkingDirectory = Path.GetDirectoryName(_serverScript!)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        PortablePythonRuntime.ConfigureProcess(psi, _python!, _pythonPath);
        psi.ArgumentList.Add(_serverScript!);
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(endpoint.Host);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(endpoint.Port.ToString());
        psi.ArgumentList.Add("--gpt-model");
        psi.ArgumentList.Add(_gptModel!);
        psi.ArgumentList.Add("--sovits-model");
        psi.ArgumentList.Add(_sovitsModel!);
        psi.ArgumentList.Add("--device");
        psi.ArgumentList.Add(_device);
        if (_half)
            psi.ArgumentList.Add("--half");

        _serverProcess = Process.Start(psi)
                         ?? throw new InvalidOperationException("无法启动常驻 TTS Python 服务");
    }

    private static async Task<Exception> CreateHttpExceptionAsync(
        string prefix, HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
                body = error.GetString() ?? body;
            else if (json.RootElement.TryGetProperty("message", out var message))
                body = message.GetString() ?? body;
        }
        catch (JsonException)
        {
            // 保留非 JSON 响应，便于排查第三方服务错误。
        }

        return new InvalidOperationException($"{prefix} ({(int)response.StatusCode}): {body}");
    }

    private static string NormalizeLanguage(string language)
        => language.Trim().ToLowerInvariant() switch
        {
            "中文" or "zh" or "zh-cn" => "zh",
            "英文" or "en" => "en",
            "日文" or "ja" => "ja",
            _ => "auto",
        };

    private static (int SampleRate, double DurationSeconds) ReadWaveMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF")
            return (24000, 0);
        _ = reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            return (24000, 0);

        var sampleRate = 24000;
        var byteRate = 0;
        long dataBytes = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunk = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (chunk == "fmt " && size >= 16)
            {
                _ = reader.ReadInt16();
                _ = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                byteRate = reader.ReadInt32();
                stream.Position += size - 12;
            }
            else if (chunk == "data")
            {
                dataBytes = size;
                break;
            }
            else
            {
                stream.Position += size;
            }

            if ((size & 1) != 0 && stream.Position < stream.Length)
                stream.Position++;
        }

        return (sampleRate, byteRate > 0 ? (double)dataBytes / byteRate : 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        _lifecycleCts.Cancel();
        Task? backgroundStart;
        lock (_backgroundStartLock)
            backgroundStart = _backgroundStart;
        if (backgroundStart is not null)
        {
            try
            {
                await backgroundStart.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // 关闭流程不应被后台模型加载异常阻塞。
            }
        }

        // 只关闭由当前桌宠启动的服务；连接到外部服务时不干预其生命周期。
        if (_serverProcess is { HasExited: false })
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var response = await _http.GetAsync("control?command=exit", timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // 我们的轻量服务没有 control 接口，关闭桌宠时终止自己创建的隐藏子进程。
            }

            if (!_serverProcess.HasExited)
                _serverProcess.Kill(entireProcessTree: true);
        }

        _serverProcess?.Dispose();
        _inferenceGate.Dispose();
        _startupGate.Dispose();
        _lifecycleCts.Dispose();
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
