using System.Text.Json;
using System.Text.Json.Serialization;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _directory;

    public SnapshotStore(string? directoryOverride = null)
    {
        _directory = directoryOverride ?? AppPaths.SnapshotsDir;
    }

    public string Directory => _directory;

    public string Save(SnapshotFile snapshot, string? path = null)
    {
        AppPaths.EnsureCreated();
        path ??= Path.Combine(_directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(snapshot.DeviceModel)}.json");
        System.IO.Directory.CreateDirectory(_directory);

        // 快照是本工具唯一的安全网，必须原子写入，避免中途失败留下半个文件。
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Options));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    public IReadOnlyList<SnapshotSummary> List()
    {
        var result = new List<SnapshotSummary>();
        if (!System.IO.Directory.Exists(_directory)) return result;

        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var snapshot = JsonSerializer.Deserialize<SnapshotFile>(json, Options);
                if (snapshot is not null)
                {
                    result.Add(new SnapshotSummary(file, snapshot));
                }
            }
            catch
            {
                // 损坏的快照文件直接跳过，不影响其它快照。
            }
        }

        return result.OrderByDescending(s => s.Snapshot.CreatedAt).ToList();
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "device";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Trim().Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    public SnapshotFile Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SnapshotFile>(json, Options)
               ?? throw new InvalidOperationException($"快照文件无法解析：{path}");
    }
}
