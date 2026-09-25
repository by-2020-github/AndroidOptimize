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
    private readonly string? _serial;
    private readonly bool _includeLegacyDirectory;

    /// <summary>
    /// serial 为空时用全局快照目录（自检、演示等场景）；
    /// 传了序列号就写到 devices\&lt;序列号&gt;\snapshots\，并且**只列这台手机自己的快照**——
    /// 把 A 手机的快照还原到 B 手机上毫无意义，还可能出事。
    /// </summary>
    public SnapshotStore(string? directoryOverride = null, string? serial = null)
    {
        _serial = string.IsNullOrWhiteSpace(serial) ? null : serial;
        _directory = directoryOverride
                     ?? (_serial is null ? AppPaths.SnapshotsDir : AppPaths.DeviceSnapshotsDir(_serial));
        // 只有「按序列号分目录」的情况下才回头看老版本留下的全局目录
        _includeLegacyDirectory = directoryOverride is null && _serial is not null;
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

        foreach (var file in EnumerateFiles())
        {
            try
            {
                var json = File.ReadAllText(file);
                var snapshot = JsonSerializer.Deserialize<SnapshotFile>(json, Options);
                if (snapshot is null) continue;

                // 按序列号分目录之后，全局目录里那些老快照也要认：但只认属于这台手机的，
                // 免得把别的手机的快照显示出来、被误还原到当前手机上。
                if (_serial is not null
                    && !string.IsNullOrWhiteSpace(snapshot.DeviceSerial)
                    && !string.Equals(snapshot.DeviceSerial, _serial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new SnapshotSummary(file, snapshot));
            }
            catch
            {
                // 损坏的快照文件直接跳过，不影响其它快照。
            }
        }

        return result.OrderByDescending(s => s.Snapshot.CreatedAt).ToList();
    }

    /// <summary>当前手机的快照目录 + 老版本留下的全局目录（升级后老快照还能还原）。</summary>
    private IEnumerable<string> EnumerateFiles()
    {
        if (System.IO.Directory.Exists(_directory))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json")) yield return file;
        }

        if (_includeLegacyDirectory && System.IO.Directory.Exists(AppPaths.SnapshotsDir))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(AppPaths.SnapshotsDir, "*.json")) yield return file;
        }
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
