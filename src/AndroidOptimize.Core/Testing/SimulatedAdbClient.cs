using System.Text;
using AndroidOptimize.Core.Adb;

namespace AndroidOptimize.Core.Testing;

/// <summary>
/// 模拟一台安卓设备的 adb 行为，用于在没有真机的情况下端到端验证
/// 「扫描 → 生成计划 → 执行 → 回读校验 → 回滚」整条链路。
/// 它只实现本程序实际用到的那些命令。
/// </summary>
public sealed class SimulatedAdbClient : AdbClient
{
    private readonly Dictionary<string, PackageState> _packages;
    private readonly Dictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _standbyBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _appOps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageState> _removedHistory = new(StringComparer.OrdinalIgnoreCase);

    public SimulatedAdbClient(string serial, IEnumerable<(string Name, bool System, bool Disabled)> packages, IDictionary<string, string?>? settings = null)
        : this(serial, packages.Select(p => new SimulatedPackage(p.Name, p.System, p.Disabled)), settings)
    {
    }

    public SimulatedAdbClient(
        string serial,
        IEnumerable<(string Name, bool System, bool Disabled, bool? NeverLaunched)> packages,
        IDictionary<string, string?>? settings = null)
        : this(serial, packages.Select(p => new SimulatedPackage(p.Name, p.System, p.Disabled, NeverLaunched: p.NeverLaunched)), settings)
    {
    }

    public SimulatedAdbClient(string serial, IEnumerable<SimulatedPackage> packages, IDictionary<string, string?>? settings = null)
        : base("simulated-adb")
    {
        Serial = serial;
        _packages = packages.ToDictionary(
            p => p.Name,
            p => new PackageState(p.Name, p.System, p.Disabled)
            {
                Installer = p.Installer,
                VersionName = p.VersionName ?? "1.0.0",
                FirstInstall = p.FirstInstall,
                NeverLaunched = p.NeverLaunched,
            },
            StringComparer.OrdinalIgnoreCase);

        if (settings is not null)
        {
            foreach (var (key, value) in settings) _settings[key] = value;
        }
    }

    public IReadOnlyDictionary<string, PackageState> Packages => _packages;
    public IReadOnlyDictionary<string, string> StandbyBuckets => _standbyBuckets;
    public IReadOnlyDictionary<string, string> AppOps => _appOps;
    public IReadOnlyList<string> ExecutedCommands { get; private set; } = [];

    public bool IsDisabled(string package) => _packages.TryGetValue(package, out var state) && state.Disabled;
    public bool IsInstalled(string package) => _packages.ContainsKey(package);
    public string? GetSetting(string key) => _settings.TryGetValue(key, out var value) ? value : null;
    public string? GetAppOp(string package, string op) =>
        _appOps.TryGetValue($"{package}|{op}", out var mode) ? mode : null;
    public string? GetStandbyBucket(string package) =>
        _standbyBuckets.TryGetValue(package, out var bucket) ? bucket : null;

    public override Task<AdbResult> RunRawAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var executed = new List<string>(ExecutedCommands);
        executed.Add(string.Join(' ', arguments));
        ExecutedCommands = executed;

        var args = arguments.Where(a => a != "-s" && a != Serial).ToList();
        if (args.Count == 0)
        {
            return Task.FromResult(Fail(string.Empty, "empty command"));
        }

        var commandLine = string.Join(' ', args);
        if (args[0] == "start-server")
        {
            return Task.FromResult(Ok(commandLine, string.Empty));
        }
        if (args[0] == "devices")
        {
            return Task.FromResult(Ok(commandLine, $"List of devices attached\n{Serial}\tdevice product:sim model:Simulated_Phone device:sim transport_id:1"));
        }
        if (args[0] == "shell" && args.Count >= 2)
        {
            return Task.FromResult(SimulateShell(commandLine, args[1]));
        }

        return Task.FromResult(Fail(commandLine, $"未知命令：{commandLine}"));
    }

    private AdbResult SimulateShell(string commandLine, string command)
    {
        var text = command.Trim();

        if (text.StartsWith("getprop", StringComparison.Ordinal)) return Ok(commandLine, Properties);
        if (text.StartsWith("pm list packages", StringComparison.Ordinal)) return Ok(commandLine, ListPackages(text));
        if (text.StartsWith("dumpsys package ", StringComparison.Ordinal)) return Ok(commandLine, DumpPackage(text[16..].Trim()));
        if (text.StartsWith("pm disable-user --user 0 ", StringComparison.Ordinal)) return SetDisabled(commandLine, text[25..].Trim(), true);
        if (text.StartsWith("pm disable --user 0 ", StringComparison.Ordinal)) return SetDisabled(commandLine, text[20..].Trim(), true);
        if (text.StartsWith("pm enable --user 0 ", StringComparison.Ordinal)) return SetDisabled(commandLine, text[19..].Trim(), false);
        if (text.StartsWith("pm uninstall -k --user 0 ", StringComparison.Ordinal)) return Uninstall(commandLine, text[25..].Trim());
        if (text.StartsWith("cmd package install-existing --user 0 ", StringComparison.Ordinal)) return InstallExisting(commandLine, text[37..].Trim());
        if (text.StartsWith("am set-standby-bucket ", StringComparison.Ordinal)) return SetBucket(commandLine, text[22..].Trim());
        if (text.StartsWith("am get-standby-bucket ", StringComparison.Ordinal)) return GetBucket(commandLine, text[22..].Trim());
        if (text.StartsWith("appops set ", StringComparison.Ordinal)) return SetAppOp(commandLine, text[11..].Trim());
        if (text.StartsWith("appops get ", StringComparison.Ordinal)) return GetAppOpCommand(commandLine, text[11..].Trim());
        if (text.StartsWith("settings get ", StringComparison.Ordinal)) return GetSetting(commandLine, text[13..].Trim());
        if (text.StartsWith("settings put ", StringComparison.Ordinal)) return PutSetting(commandLine, text[13..].Trim());
        if (text.StartsWith("settings delete ", StringComparison.Ordinal)) return DeleteSetting(commandLine, text[16..].Trim());

        return Fail(commandLine, $"模拟器未实现：{text}");
    }

    private AdbResult SetDisabled(string commandLine, string package, bool disabled)
    {
        if (!_packages.TryGetValue(package, out var state))
        {
            return Fail(commandLine, $"Error: java.lang.IllegalArgumentException: Unknown package: {package}");
        }

        state.Disabled = disabled;
        return Ok(commandLine, $"Package {package} new state: {(disabled ? "disabled-user" : "enabled")}");
    }

    private AdbResult Uninstall(string commandLine, string package)
    {
        if (!_packages.TryGetValue(package, out var state))
        {
            return Fail(commandLine, "Failure [not installed for 0]");
        }

        _packages.Remove(package);
        _removedHistory[package] = state;
        return Ok(commandLine, "Success");
    }

    private AdbResult InstallExisting(string commandLine, string package)
    {
        if (_packages.ContainsKey(package))
        {
            return Ok(commandLine, $"Package {package} installed for user: 0");
        }

        if (!_removedHistory.TryGetValue(package, out var state))
        {
            return Fail(commandLine, $"Failure [package {package} not found]");
        }

        _packages[package] = state;
        _removedHistory.Remove(package);
        return Ok(commandLine, $"Package {package} installed for user: 0");
    }

    private AdbResult SetBucket(string commandLine, string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return Fail(commandLine, "Error: bad arguments");
        if (!_packages.ContainsKey(parts[0])) return Fail(commandLine, $"Error: package {parts[0]} not found");
        _standbyBuckets[parts[0]] = parts[1];
        return Ok(commandLine, string.Empty);
    }

    private AdbResult SetAppOp(string commandLine, string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return Fail(commandLine, "Error: bad appops arguments");
        _appOps[$"{parts[0]}|{parts[1]}"] = parts[2];
        return Ok(commandLine, string.Empty);
    }

    private AdbResult GetBucket(string commandLine, string package)
    {
        var bucket = GetStandbyBucket(package);
        return Ok(commandLine, bucket ?? $"{package}: 10");
    }

    private AdbResult GetAppOpCommand(string commandLine, string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return Fail(commandLine, "Error: bad appops arguments");

        var mode = GetAppOp(parts[0], parts[1]);
        return Ok(commandLine, mode is null ? "No operations." : $"{parts[1]}: {mode}");
    }

    private AdbResult GetSetting(string commandLine, string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return Fail(commandLine, "Error: bad arguments");
        var value = _settings.TryGetValue($"{parts[0]}/{parts[1]}", out var stored) ? stored : null;
        return Ok(commandLine, value ?? "null");
    }

    private AdbResult PutSetting(string commandLine, string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return Fail(commandLine, "Error: bad arguments");
        _settings[$"{parts[0]}/{parts[1]}"] = parts[2];
        return Ok(commandLine, string.Empty);
    }

    private AdbResult DeleteSetting(string commandLine, string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return Fail(commandLine, "Error: bad arguments");
        _settings.Remove($"{parts[0]}/{parts[1]}");
        return Ok(commandLine, string.Empty);
    }

    private string ListPackages(string command)
    {
        var includeUninstalled = command.Contains(" -u", StringComparison.Ordinal);
        var systemOnly = command.Contains(" -s", StringComparison.Ordinal);
        var thirdOnly = command.Contains(" -3", StringComparison.Ordinal);
        var disabledOnly = command.Contains(" -d", StringComparison.Ordinal);
        var enabledOnly = command.Contains(" -e", StringComparison.Ordinal);
        var showVersion = command.Contains("--show-versioncode", StringComparison.Ordinal);

        var builder = new StringBuilder();
        var names = includeUninstalled
            ? _packages.Keys.Concat(_removedHistory.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            : _packages.Keys.AsEnumerable();

        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var state = _packages.TryGetValue(name, out var found) ? found : null;
            var installed = state is not null;
            var isSystem = state?.IsSystem ?? true;
            var disabled = state?.Disabled ?? false;

            if (systemOnly && !isSystem) continue;
            if (thirdOnly && isSystem) continue;
            if (disabledOnly && !(installed && disabled)) continue;
            if (enabledOnly && !(installed && !disabled)) continue;

            builder.Append("package:").Append(name);
            if (showVersion && installed) builder.Append(" versionCode:1000");
            builder.Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    private string DumpPackage(string package)
    {
        if (!_packages.TryGetValue(package, out var state)) return $"Package [{package}]: not found";

        var builder = new StringBuilder();
        builder.AppendLine($"Package [{package}]:");
        builder.AppendLine($"  codePath={(state.IsSystem ? "/system/priv-app/" : "/data/app/")}{package}/base.apk");
        builder.AppendLine($"  versionName={state.VersionName}");
        builder.AppendLine($"  firstInstallTime={state.FirstInstall ?? "2025-11-02 09:15:33"}");
        builder.AppendLine("  lastUpdateTime=2025-12-20 18:02:11");
        var installer = state.Installer ?? (state.IsSystem ? null : "com.xiaomi.market");
        if (installer is not null) builder.AppendLine($"  installerPackageName={installer}");
        var notLaunched = state.NeverLaunched is null ? string.Empty : $" notLaunched={state.NeverLaunched.Value.ToString().ToLowerInvariant()}";
        builder.AppendLine($"  User 0: ceDataInode=12345 installed=true hidden=false suspended=false stopped=false" +
                           $"{notLaunched} enabled={(state.Disabled ? 3 : 0)}");
        return builder.ToString();
    }

    private const string Properties = """
        [ro.product.brand]: [Redmi]
        [ro.product.manufacturer]: [Xiaomi]
        [ro.product.model]: [Redmi Note 12 Turbo]
        [ro.product.device]: [marble]
        [ro.product.cpu.abi]: [arm64-v8a]
        [ro.build.version.release]: [14]
        [ro.build.version.sdk]: [34]
        [ro.build.display.id]: [UKQ1.231003.002]
        [ro.mi.os.version.name]: [1.0.8.0]
        """;

    private static AdbResult Ok(string commandLine, string stdout) => new()
    {
        CommandLine = commandLine,
        ExitCode = 0,
        StdOut = stdout,
        StdErr = string.Empty,
        Duration = TimeSpan.FromMilliseconds(1),
    };

    private static AdbResult Fail(string commandLine, string stderr) => new()
    {
        CommandLine = commandLine,
        ExitCode = 1,
        StdOut = string.Empty,
        StdErr = stderr,
        Duration = TimeSpan.FromMilliseconds(1),
    };

    public sealed record SimulatedPackage(
        string Name,
        bool System,
        bool Disabled = false,
        string? Installer = null,
        string? VersionName = null,
        string? FirstInstall = null,
        /// <summary>模拟 dumpsys 里的 notLaunched；null 表示这台设备不输出该字段。</summary>
        bool? NeverLaunched = null);

    public sealed class PackageState(string name, bool isSystem, bool disabled)
    {
        public string Name { get; } = name;
        public bool IsSystem { get; } = isSystem;
        public bool Disabled { get; set; } = disabled;
        public string? Installer { get; init; }
        public string VersionName { get; init; } = "1.0.0";
        public string? FirstInstall { get; init; }
        /// <summary>模拟 dumpsys 里的 notLaunched 字段；null 表示这台设备不输出该字段。</summary>
        public bool? NeverLaunched { get; set; }
    }
}
