using System.Text.Json;
using System.Text.Json.Serialization;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>DPAPI 加密后的 API Key（Base64）。绝不存明文。</summary>
    [JsonPropertyName("apiKeyProtected")]
    public string? ApiKeyProtected { get; set; }

    [JsonPropertyName("enableAi")] public bool EnableAi { get; set; }
    [JsonPropertyName("deepSeekBaseUrl")] public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com";
    [JsonPropertyName("deepSeekModel")] public string DeepSeekModel { get; set; } = "deepseek-chat";
    [JsonPropertyName("aiMaxPackages")] public int AiMaxPackages { get; set; } = 60;
    [JsonPropertyName("consentToSendPackageNames")] public bool ConsentToSendPackageNames { get; set; }

    [JsonPropertyName("safetyMode")] public bool SafetyMode { get; set; } = true;
    [JsonPropertyName("deepScan")] public bool DeepScan { get; set; } = true;
    /// <summary>强制重新读取权限与安装来源（忽略缓存）。默认关，用缓存能显著加快重复扫描。</summary>
    [JsonPropertyName("forceDetailRefresh")] public bool ForceDetailRefresh { get; set; }

    /// <summary>
    /// 卸载前把安装包备份到电脑（默认开）。
    /// 商店安装的应用被卸载时安装包会被系统删掉，没有备份就再也装不回来。
    /// </summary>
    [JsonPropertyName("backupApksBeforeUninstall")] public bool BackupApksBeforeUninstall { get; set; } = true;
    [JsonPropertyName("includeSettings")] public bool IncludeSettings { get; set; } = true;
    [JsonPropertyName("allowGuarded")] public bool AllowGuarded { get; set; }
    [JsonPropertyName("lastTier")] public OptimizationTier LastTier { get; set; } = OptimizationTier.Normal;
    [JsonPropertyName("animationScale")] public string AnimationScale { get; set; } = "0.75";

    /// <summary>
    /// 被用户关掉的全局策略 ID。
    /// 刻意记录「关掉的」而不是「开启的」：这样以后名单里新增策略时，老用户也能自动拿到。
    /// </summary>
    [JsonPropertyName("disabledPolicies")] public List<string> DisabledPolicies { get; set; } = [];

    [JsonIgnore]
    public string? ApiKey
    {
        get => SecretProtector.Unprotect(ApiKeyProtected);
        set
        {
            ApiKeyProtected = SecretProtector.Protect(value?.Trim());
            _hasApiKey = null;
        }
    }

    private bool? _hasApiKey;

    /// <summary>
    /// 不只是判断"有没有存过 Key"，而是判断"在这台电脑上能不能真的解开"。
    /// 配置文件被拷到另一台电脑时 DPAPI 无法解密，这里会如实返回 false。
    /// </summary>
    [JsonIgnore]
    public bool HasApiKey => _hasApiKey ??= !string.IsNullOrWhiteSpace(ApiKey);

    [JsonIgnore]
    public string FilePath { get; private set; } = AppPaths.SettingsFile;

    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null)
                {
                    settings.FilePath = path;
                    return settings;
                }
            }
        }
        catch
        {
            // 配置损坏时回到默认值，不影响主流程。
        }

        return new AppSettings { FilePath = path };
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        AppPaths.EnsureCreated();
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
    }
}
