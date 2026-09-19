using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AndroidOptimize.Core.Models;
using AndroidOptimize.Core.Services;

namespace AndroidOptimize.Core.Ai;

/// <summary>
/// 用 DeepSeek 识别名单里没有记录的应用。
/// 设计原则：只做「命名与归类」，不做决策；结果一律默认不勾选，必须由用户确认。
/// </summary>
public sealed class DeepSeekClassifier : IPackageClassifier, IDisposable
{
    private const int BatchSize = 25;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private const string SystemPrompt = """
        你是安卓预装应用与广告软件识别专家，服务于一个帮助中国中老年用户清理手机的桌面工具。
        用户会给你一批安卓应用的包名与元数据（JSON 数组）。请判断每个应用是什么，以及是否建议处理。

        你必须只输出 JSON 对象，不要输出任何解释文字，格式为：
        {"results":[{"package":"包名","category":"分类","action":"keep|disable|uninstall|restrict|unknown","confidence":0.0,"reason":"一句中文说明"}]}

        判定规则：
        1. 无法确定用途时，必须 action 为 "unknown" 且 confidence 不高于 0.3，禁止猜测。
        2. 以下类型一律 action 为 "keep"：支付、银行、政务、医保社保、反诈、无障碍、输入法、电话短信联系人、系统框架、相机相册、应用安装器、安全中心。
        3. 只有确认是广告、推送、遥测统计、推广应用分发、或电商/短视频类的后台常驻组件时，才可以使用 "disable" 或 "restrict"。
        4. "uninstall" 只用于明确可卸载的预装娱乐类应用（视频、游戏、商城、浏览器）。
        5. isSystem 为 true 的应用要保守，不确定就给 "keep"。
        6. category 使用简短中文，例如：广告与跟踪 / 应用分发 / 快应用 / 游戏与娱乐 / 预装应用 / 支付 / 系统组件 / 未知。
        7. reason 必须是一句普通话，说明它是什么以及为什么这样建议，不超过 40 个字。
        8. results 数组的条目数必须与输入一致，package 字段必须原样返回。
        """;

    private readonly AppSettings _settings;
    private readonly ClassificationCache _cache;
    private readonly RunLogger _log;
    private readonly HttpClient _http;

    public DeepSeekClassifier(AppSettings settings, ClassificationCache cache, RunLogger log, HttpClient? http = null)
    {
        _settings = settings;
        _cache = cache;
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    public bool IsAvailable =>
        _settings.EnableAi
        && _settings.ConsentToSendPackageNames
        && !string.IsNullOrWhiteSpace(_settings.ApiKey);

    public string UnavailableReason
    {
        get
        {
            if (!_settings.EnableAi) return "未开启 AI 识别。";
            if (string.IsNullOrWhiteSpace(_settings.ApiKey)) return "未填写 DeepSeek API Key。";
            if (!_settings.ConsentToSendPackageNames) return "尚未同意把未知应用包名发送到 DeepSeek 进行识别。";
            return string.Empty;
        }
    }

    public async Task<IReadOnlyList<PackageClassification>> ClassifyAsync(
        IReadOnlyList<UnknownPackage> packages,
        DeviceInfo device,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<PackageClassification>();
        if (packages.Count == 0) return results;

        var model = _settings.DeepSeekModel;
        var pending = new List<UnknownPackage>();

        foreach (var package in packages)
        {
            var cached = _cache.Get(package.PackageName, model);
            if (cached is not null)
            {
                results.Add(cached);
            }
            else
            {
                pending.Add(package);
            }
        }

        if (pending.Count > 0)
        {
            _log.Info($"未知应用 {packages.Count} 个：命中缓存 {results.Count} 个，需要请求 {pending.Count} 个。");
        }

        if (!IsAvailable)
        {
            if (pending.Count > 0)
            {
                _log.Warn($"跳过 AI 识别：{UnavailableReason}");
            }
            return results;
        }

        var batches = pending.Chunk(BatchSize).ToList();
        for (var i = 0; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"AI 识别未知名应用…（{i + 1}/{batches.Count}）");

            try
            {
                var batchResults = await ClassifyBatchAsync(batches[i], device, ct).ConfigureAwait(false);
                foreach (var classification in batchResults)
                {
                    _cache.Set(classification, model);
                    results.Add(classification);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn($"AI 识别第 {i + 1} 批失败：{ex.Message}");
            }
        }

        _cache.Save();
        return results;
    }

    private async Task<IReadOnlyList<PackageClassification>> ClassifyBatchAsync(
        IReadOnlyList<UnknownPackage> batch,
        DeviceInfo device,
        CancellationToken ct)
    {
        var payload = new
        {
            model = _settings.DeepSeekModel,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = BuildUserMessage(batch, device) },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint())
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DeepSeek 返回 {(int)response.StatusCode}：{Truncate(body, 300)}");
        }

        return ParseResponse(body, batch);
    }

    private string BuildEndpoint() => $"{_settings.DeepSeekBaseUrl.TrimEnd('/')}/chat/completions";

    private static string BuildUserMessage(IReadOnlyList<UnknownPackage> batch, DeviceInfo device)
    {
        var items = batch.Select(p => new
        {
            package = p.PackageName,
            version = p.VersionName,
            installer = p.Installer,
            firstInstall = p.FirstInstallTime?.ToString("yyyy-MM-dd"),
            isSystem = p.IsSystem,
        });

        return $"设备：{device.Brand} {device.Model}，系统 {device.RomName} {device.RomVersion} / Android {device.AndroidRelease}。" +
               $"以下 {batch.Count} 个应用请逐个判断：\n{JsonSerializer.Serialize(items, Json)}";
    }

    private static IReadOnlyList<PackageClassification> ParseResponse(string body, IReadOnlyList<UnknownPackage> batch)
    {
        var allowed = new HashSet<string>(batch.Select(p => p.PackageName), StringComparer.OrdinalIgnoreCase);
        var results = new List<PackageClassification>();

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("DeepSeek 返回内容缺少 choices。");
        }

        var content = choices[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) return results;

        using var parsed = JsonDocument.Parse(ExtractJson(content));
        if (!parsed.RootElement.TryGetProperty("results", out var array)) return results;

        foreach (var element in array.EnumerateArray())
        {
            var name = element.TryGetProperty("package", out var p) ? p.GetString() : null;

            // 只接受我们问过的包名，避免模型编造条目。
            if (string.IsNullOrWhiteSpace(name) || !allowed.Contains(name)) continue;

            var actionText = element.TryGetProperty("action", out var a) ? a.GetString() ?? "unknown" : "unknown";
            var confidence = element.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                ? Math.Clamp(c.GetDouble(), 0, 1)
                : 0.0;
            var action = actionText.ToLowerInvariant() switch
            {
                "disable" when confidence >= 0.5 => PackageAction.Disable,
                "uninstall" when confidence >= 0.5 => PackageAction.Uninstall,
                "restrict" when confidence >= 0.5 => PackageAction.Restrict,
                _ => PackageAction.Keep,
            };

            var category = element.TryGetProperty("category", out var cat) ? cat.GetString() ?? "未知" : "未知";
            var reason = element.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;

            results.Add(new PackageClassification
            {
                PackageName = name,
                Category = Sanitize(category),
                Action = action,
                Confidence = confidence,
                Reason = Sanitize(reason),
                Source = "ai",
            });
        }

        return results;
    }

    /// <summary>模型偶尔会包一层 ```json，这里做容错。</summary>
    private static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }

    /// <summary>应用名属于不可信输入，截断并去掉换行，避免污染展示与日志。</summary>
    private static string Sanitize(string value)
    {
        var flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length > 60 ? flat[..60] : flat;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    public void Dispose() => _http.Dispose();
}
