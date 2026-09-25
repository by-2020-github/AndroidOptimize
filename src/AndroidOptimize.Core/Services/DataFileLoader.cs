using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

/// <summary>
/// 数据文件的统一加载方式：用户数据目录 → 程序目录 → 程序内置副本。
/// 这样即使离线、即使配置文件被删，程序依然能工作；将来做在线更新时只要往第一级写文件即可。
/// </summary>
internal static class DataFileLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    public static T? Load<T>(string fileName, string appBaseDirectory, out ListSource source, List<string> warnings)
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
                var parsed = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
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
        if (embedded is not null) source = ListSource.Builtin;
        return embedded;
    }

    private static T? LoadEmbedded<T>(string fileName, List<string> warnings) where T : class
    {
        var assembly = typeof(DataFileLoader).Assembly;
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
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception ex)
        {
            warnings.Add($"解析内置 {fileName} 失败：{ex.Message}");
            return null;
        }
    }
}
