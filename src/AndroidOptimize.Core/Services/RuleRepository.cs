using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

/// <summary>
/// 名单仓库。数据来源优先级：用户数据目录（后续在线更新写这里）→ 程序目录 data → 内置资源。
/// 这样即使离线、即使配置文件被删，程序依然能工作。
/// </summary>
public sealed class RuleRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    private readonly List<(Regex Regex, ProtectionPattern Pattern, int Specificity)> _protectionMatchers;
    private readonly Dictionary<string, List<RuleRecord>> _packageRules;
    private readonly Dictionary<string, RuleRecord> _settingRules;

    private RuleRepository(
        RuleSetFile ruleSet,
        ProtectionFile protection,
        ListSource ruleSource,
        ListSource protectionSource,
        List<string> warnings)
    {
        RuleSet = ruleSet;
        Protection = protection;
        RuleSource = ruleSource;
        ProtectionSource = protectionSource;
        Warnings = warnings;

        _protectionMatchers = protection.Patterns
            .Select(p => (BuildRegex(p.Match), p, Specificity(p.Match)))
            .OrderByDescending(x => x.Item3)
            .ToList();

        _packageRules = new Dictionary<string, List<RuleRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in ruleSet.Packages)
        {
            if (string.IsNullOrWhiteSpace(rule.Id)) continue;
            if (!_packageRules.TryGetValue(rule.Id, out var list))
            {
                list = [];
                _packageRules[rule.Id] = list;
            }
            list.Add(rule);
        }

        _settingRules = new Dictionary<string, RuleRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in ruleSet.Settings)
        {
            if (string.IsNullOrWhiteSpace(rule.Id)) continue;
            _settingRules[rule.Id] = rule;
        }
    }

    public RuleSetFile RuleSet { get; }
    public ProtectionFile Protection { get; }
    public ListSource RuleSource { get; }
    public ListSource ProtectionSource { get; }
    public IReadOnlyList<string> Warnings { get; }

    public string ListVersion => RuleSet.ListVersion;
    public int PackageRuleCount => RuleSet.Packages.Count;
    public int SettingRuleCount => RuleSet.Settings.Count;
    public int ProtectionRuleCount => Protection.Patterns.Count;

    public string SourceDescription
    {
        get
        {
            string Name(ListSource source) => source switch
            {
                ListSource.UserData => "用户数据目录",
                ListSource.AppFolder => "程序目录 data",
                _ => "内置名单",
            };
            return $"名单 {RuleSet.ListVersion}（{Name(RuleSource)}）· 保护规则 {Protection.ListVersion}（{Name(ProtectionSource)}）";
        }
    }

    public static RuleRepository Load(string? appBaseDirectory = null)
    {
        var warnings = new List<string>();
        appBaseDirectory ??= AppContext.BaseDirectory;

        var ruleSet = LoadFile<RuleSetFile>("packages.json", appBaseDirectory, out var ruleSource, warnings)
                      ?? throw new InvalidOperationException("无法加载应用名单 packages.json，程序内置资源可能已损坏。");
        var protection = LoadFile<ProtectionFile>("protected.json", appBaseDirectory, out var protectionSource, warnings)
                         ?? CreateDefaultProtection();

        if (ruleSet.SchemaVersion > 1)
        {
            warnings.Add($"名单 schemaVersion={ruleSet.SchemaVersion} 高于本程序支持的版本（1），可能存在无法识别的字段。");
        }

        return new RuleRepository(ruleSet, protection, ruleSource, protectionSource, warnings);
    }

    /// <summary>内置资源缺失时的兜底保护名单：宁可什么都不做，也不能误删关键组件。</summary>
    private static ProtectionFile CreateDefaultProtection()
    {
        string[] critical =
        [
            "android", "com.android.systemui", "com.android.settings", "com.android.shell",
            "com.android.providers.*", "com.android.packageinstaller", "com.miui.packageinstaller",
            "com.android.phone", "com.android.contacts", "com.android.mms", "*inputmethod*",
            "com.android.*", "com.miui.security*", "com.miui.home", "*launcher*",
        ];

        return new ProtectionFile
        {
            SchemaVersion = 1,
            ListVersion = "fallback",
            Patterns = critical
                .Select(m => new ProtectionPattern
                {
                    Match = m,
                    Level = ProtectionLevel.Absolute,
                    Category = "兜底保护",
                    Reason = "内置保护名单不可用时的兜底规则。",
                })
                .ToList(),
        };
    }

    private static T? LoadFile<T>(string fileName, string appBaseDirectory, out ListSource source, List<string> warnings)
        where T : class
    {
        source = ListSource.Builtin;

        var candidates = new (string Path, ListSource Source)[]
        {
            (Path.Combine(AppPaths.UserDataDir, fileName), ListSource.UserData),
            (Path.Combine(appBaseDirectory, "data", fileName), ListSource.AppFolder),
        };

        foreach (var (path, candidateSource) in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize<T>(json, JsonOptions);
                if (parsed is not null)
                {
                    source = candidateSource;
                    return parsed;
                }
                warnings.Add($"配置文件 {path} 内容为空，已跳过。");
            }
            catch (Exception ex)
            {
                warnings.Add($"读取 {path} 失败（{ex.Message}），已跳过该来源。");
            }
        }

        var embedded = LoadEmbedded<T>(fileName, warnings);
        if (embedded is not null)
        {
            source = ListSource.Builtin;
        }
        return embedded;
    }

    private static T? LoadEmbedded<T>(string fileName, List<string> warnings) where T : class
    {
        var assembly = typeof(RuleRepository).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            warnings.Add($"内置资源中找不到 {fileName}。");
            return null;
        }

        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            return JsonSerializer.Deserialize<T>(stream, JsonOptions);
        }
        catch (Exception ex)
        {
            warnings.Add($"解析内置 {fileName} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 保护名单匹配。精确匹配优先于通配符，通配符中更长的模式优先。
    /// </summary>
    public ProtectionMatch MatchProtection(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return ProtectionMatch.None;

        foreach (var (regex, pattern, _) in _protectionMatchers)
        {
            if (regex.IsMatch(packageName))
            {
                return new ProtectionMatch(pattern.Level, pattern);
            }
        }

        return ProtectionMatch.None;
    }

    public RuleRecord? FindPackageRule(string packageName, IReadOnlyList<string> brandTokens)
    {
        if (!_packageRules.TryGetValue(packageName, out var rules)) return null;

        RuleRecord? wildcardFallback = null;
        foreach (var rule in rules)
        {
            if (rule.MatchesBrand(brandTokens))
            {
                if (rule.Brands.Contains("*", StringComparer.Ordinal)) wildcardFallback ??= rule;
                else return rule;
            }
        }

        return wildcardFallback;
    }

    public RuleRecord? FindSettingRule(string id) =>
        _settingRules.TryGetValue(id, out var rule) ? rule : null;

    public IEnumerable<RuleRecord> AllPackageRules => RuleSet.Packages;
    public IEnumerable<RuleRecord> AllSettingRules => RuleSet.Settings;
    public IReadOnlyList<PolicyRecord> Policies => RuleSet.Policies;

    /// <summary>策略里的排除匹配（支持 * 通配符）。</summary>
    public bool IsExcludedByPolicy(PolicyRecord policy, string packageName)
    {
        foreach (var pattern in policy.Exclude)
        {
            if (MatchesGlob(pattern, packageName)) return true;
        }
        return false;
    }

    /// <summary>
    /// 名单里没有记录、但明显属于正常应用（地图、健康、出行、办公等），不该被当成垃圾。
    /// </summary>
    public bool IsCommonApp(string packageName)
    {
        foreach (var pattern in RuleSet.UnknownExclude)
        {
            if (MatchesGlob(pattern, packageName)) return true;
        }
        return false;
    }

    public LazyModeConfig LazyMode => RuleSet.LazyMode ?? new LazyModeConfig();

    /// <summary>
    /// 懒人模式下要保留的应用：
    /// 保护名单里的应用一律保留；此外再看懒人白名单（支持通配符）。
    /// </summary>
    public bool IsLazyModeKeep(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return true;

        // 保护名单优先级最高：反诈、支付、输入法、电话短信等永远不会被懒人模式清掉。
        if (MatchProtection(packageName).IsProtected) return true;

        foreach (var pattern in LazyMode.Keep)
        {
            if (MatchesGlob(pattern, packageName)) return true;
        }

        return false;
    }

    public static bool MatchesGlob(string pattern, string value)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(value)) return false;
        if (pattern == "*") return true;
        return BuildRegex(pattern).IsMatch(value);
    }

    private static Regex BuildRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace(@"\*", ".*");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static int Specificity(string pattern) =>
        (pattern.Contains('*') ? 0 : 1000) + pattern.Length;
}
