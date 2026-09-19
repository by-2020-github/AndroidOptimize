using System.Globalization;
using System.Text.RegularExpressions;
using AndroidOptimize.Core.Adb;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

public sealed class DeviceException : Exception
{
    public DeviceException(string message, string hint) : base(message) => Hint = hint;
    public string Hint { get; }
}

public sealed partial class DeviceScanner
{
    private readonly AdbClient _adb;

    public DeviceScanner(AdbClient adb) => _adb = adb;

    /// <summary>确认有且仅有一台可用设备，返回其序列号。失败时抛出带中文提示的 DeviceException。</summary>
    public async Task<AdbDevice> EnsureDeviceAsync(CancellationToken ct = default)
    {
        var startServer = await _adb.RunRawAsync(["start-server"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!startServer.Ok && startServer.StdErr.Contains("cannot", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeviceException("ADB 服务启动失败。", $"请确认 {_adb.AdbPath} 可以正常运行，或杀毒软件没有拦截它。");
        }

        var devices = await ListDevicesAsync(ct).ConfigureAwait(false);
        if (devices.Count == 0)
        {
            throw new DeviceException(
                "没有检测到手机。",
                "请依次检查：\n1) 用数据线连接电脑，并选择「传输文件 / MTP」而不是「仅充电」；\n2) 手机上打开 设置 → 关于手机 → 连续点击「版本号」7 次，开启开发者选项；\n3) 在 开发者选项 中打开「USB 调试」，小米/红米还需要额外打开「USB 调试（安全设置）」；\n4) 拔插一次数据线，手机弹出「允许 USB 调试吗」时勾选「一律允许」并点允许。");
        }

        var usable = devices.Where(d => d.IsUsable).ToList();
        if (usable.Count == 0)
        {
            var unauthorized = devices.FirstOrDefault(d => d.IsUnauthorized);
            if (unauthorized is not null)
            {
                throw new DeviceException(
                    "手机已连接，但还没有授权这台电脑。",
                    "请点亮手机屏幕，在「允许 USB 调试吗？」弹窗中勾选「一律允许使用这台计算机进行调试」，然后点「允许」。\n如果没看到弹窗：拔插数据线，或在开发者选项中点「撤销 USB 调试授权」后重试。");
            }

            var offline = devices.FirstOrDefault(d => d.IsOffline);
            if (offline is not null)
            {
                throw new DeviceException(
                    "手机处于离线状态。",
                    "请拔插数据线重试；如果仍然离线，在电脑上执行 adb kill-server 后重新连接，或换一根数据线（充电线常常不支持数据传输）。");
            }

            throw new DeviceException("没有可用的手机连接。", "请确认数据线支持数据传输，并已授权 USB 调试。");
        }

        if (usable.Count > 1 && string.IsNullOrEmpty(_adb.Serial))
        {
            var list = string.Join("\n", usable.Select(d => $"  · {d.Serial}  {d.Model}"));
            throw new DeviceException(
                $"检测到 {usable.Count} 台手机，请只保留一台。",
                $"当前连接：\n{list}\n请拔掉多余的手机后重试。");
        }

        return usable[0];
    }

    public async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var result = await _adb.RunRawAsync(["devices", "-l"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var devices = new List<AdbDevice>();

        foreach (var line in result.Lines)
        {
            if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith('*')) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string? model = null, product = null, transport = null;
            foreach (var part in parts.Skip(2))
            {
                if (part.StartsWith("model:", StringComparison.OrdinalIgnoreCase)) model = part[6..];
                else if (part.StartsWith("product:", StringComparison.OrdinalIgnoreCase)) product = part[8..];
                else if (part.StartsWith("transport_id:", StringComparison.OrdinalIgnoreCase)) transport = part[13..];
            }

            devices.Add(new AdbDevice
            {
                Serial = parts[0],
                State = parts[1],
                Model = model?.Replace('_', ' '),
                Product = product,
                TransportId = transport,
            });
        }

        return devices;
    }

    public async Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        var result = await _adb.ShellAsync("getprop", TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
        if (!result.Ok && string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new DeviceException("读取手机信息失败。", "请确认手机已解锁并保持屏幕常亮后重试。");
        }

        var props = ParseProperties(result.StdOut);

        var brand = Get(props, "ro.product.brand") ?? Get(props, "ro.product.vendor.brand") ?? string.Empty;
        var manufacturer = Get(props, "ro.product.manufacturer") ?? string.Empty;
        var model = Get(props, "ro.product.model") ?? string.Empty;
        var device = Get(props, "ro.product.device") ?? string.Empty;

        var (romName, romVersion) = DetectRom(props);

        return new DeviceInfo
        {
            Serial = _adb.Serial ?? "unknown",
            Brand = brand.Trim(),
            Manufacturer = manufacturer.Trim(),
            Model = model.Trim(),
            Device = device.Trim(),
            AndroidRelease = Get(props, "ro.build.version.release") ?? "?",
            SdkInt = int.TryParse(Get(props, "ro.build.version.sdk"), out var sdk) ? sdk : 0,
            BuildId = Get(props, "ro.build.display.id") ?? Get(props, "ro.build.id") ?? "?",
            RomName = romName,
            RomVersion = romVersion,
            CpuAbi = Get(props, "ro.product.cpu.abi") ?? "?",
            BrandTokens = BuildBrandTokens(brand, manufacturer),
        };
    }

    /// <summary>
    /// 采集所有包的状态。只做 6 次批量查询；深度扫描才会逐个读取第三方应用的安装来源。
    /// </summary>
    public async Task<PackageSnapshot> ScanPackagesAsync(
        DeviceInfo device,
        bool deepScan,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        progress?.Report("读取已安装应用列表…");
        var installed = await ListPackagesAsync("--user 0 --show-versioncode", warnings, ct).ConfigureAwait(false);
        if (installed.Count == 0)
        {
            installed = await ListPackagesAsync("--user 0", warnings, ct).ConfigureAwait(false);
        }

        progress?.Report("读取历史安装记录…");
        var everKnown = await ListPackagesAsync("-u --user 0", warnings, ct).ConfigureAwait(false);

        progress?.Report("读取系统应用与第三方应用分类…");
        var system = await ListPackagesAsync("-s --user 0", warnings, ct).ConfigureAwait(false);
        var thirdParty = await ListPackagesAsync("-3 --user 0", warnings, ct).ConfigureAwait(false);

        progress?.Report("读取启用与停用状态…");
        var disabled = await ListPackagesAsync("-d --user 0", warnings, ct).ConfigureAwait(false);

        var map = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);

        var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        allNames.UnionWith(installed.Keys);
        allNames.UnionWith(everKnown.Keys);

        foreach (var name in allNames)
        {
            var isInstalled = installed.ContainsKey(name);
            map[name] = new PackageEntry
            {
                Name = name,
                Installed = isInstalled,
                Disabled = isInstalled && disabled.ContainsKey(name),
                RemovedForUser = !isInstalled,
                IsSystem = system.ContainsKey(name),
                IsThirdParty = thirdParty.ContainsKey(name),
                VersionName = installed.TryGetValue(name, out var vc) ? vc : null,
            };
        }

        var enriched = false;
        if (deepScan)
        {
            var targets = map.Values
                .Where(p => p.Installed && p.IsThirdParty)
                .Select(p => p.Name)
                .ToList();

            if (targets.Count == 0)
            {
                progress?.Report("没有需要深度扫描的第三方应用。");
            }
            else if (targets.Count > 150)
            {
                warnings.Add($"第三方应用过多（{targets.Count} 个），已跳过安装来源深度扫描以保证速度。");
            }
            else
            {
                progress?.Report($"深度扫描 {targets.Count} 个第三方应用的安装来源…");
                var details = await EnrichAsync(targets, progress, ct).ConfigureAwait(false);
                foreach (var (name, detail) in details)
                {
                    if (map.TryGetValue(name, out var existing))
                    {
                        map[name] = existing with
                        {
                            Installer = detail.Installer,
                            FirstInstallTime = detail.FirstInstallTime,
                            LastUpdateTime = detail.LastUpdateTime,
                            CodePath = detail.CodePath,
                            VersionName = detail.VersionName ?? existing.VersionName,
                            NeverLaunched = detail.NeverLaunched,
                        };
                    }
                }
                enriched = true;
            }
        }

        return new PackageSnapshot
        {
            Device = device,
            ScannedAt = DateTimeOffset.Now,
            Packages = map,
            SystemCount = system.Count,
            ThirdPartyCount = thirdParty.Count,
            DetailEnriched = enriched,
            Warnings = warnings,
        };
    }

    private async Task<Dictionary<string, PackageDetail>> EnrichAsync(
        IReadOnlyList<string> packages,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var result = new Dictionary<string, PackageDetail>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        using var gate = new SemaphoreSlim(4);

        var tasks = packages.Select(async name =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var dump = await _adb.ShellAsync($"dumpsys package {name}", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                var detail = ParsePackageDetail(name, dump.StdOut);
                lock (result)
                {
                    result[name] = detail;
                    completed++;
                }
                if (completed % 10 == 0)
                {
                    progress?.Report($"深度扫描进行中… {completed}/{packages.Count}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单个包读取失败不影响整体扫描。
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    private static PackageDetail ParsePackageDetail(string packageName, string dump)
    {
        string? installer = null, versionName = null, codePath = null;
        DateTimeOffset? firstInstall = null, lastUpdate = null;
        bool? neverLaunched = null;

        foreach (var rawLine in dump.Split('\n'))
        {
            var line = rawLine.Trim();
            if (installer is null && line.StartsWith("installerPackageName=", StringComparison.Ordinal))
            {
                installer = NullIfEmpty(line[21..]);
            }
            else if (versionName is null && line.StartsWith("versionName=", StringComparison.Ordinal))
            {
                versionName = NullIfEmpty(line[12..]);
            }
            else if (codePath is null && line.StartsWith("codePath=", StringComparison.Ordinal))
            {
                codePath = NullIfEmpty(line[9..]);
            }
            else if (firstInstall is null && line.StartsWith("firstInstallTime=", StringComparison.Ordinal))
            {
                firstInstall = ParseDeviceTime(line[17..]);
            }
            else if (lastUpdate is null && line.StartsWith("lastUpdateTime=", StringComparison.Ordinal))
            {
                lastUpdate = ParseDeviceTime(line[15..]);
            }
            else if (neverLaunched is null && line.Contains("notLaunched=", StringComparison.Ordinal))
            {
                // 形如：User 0: ceDataInode=... installed=true hidden=false ... notLaunched=true enabled=0
                var marker = line.IndexOf("notLaunched=", StringComparison.Ordinal) + 12;
                var value = line[marker..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (bool.TryParse(value, out var parsed))
                {
                    neverLaunched = parsed;
                }
            }
        }

        return new PackageDetail(packageName, installer, versionName, codePath, firstInstall, lastUpdate, neverLaunched);
    }

    private static DateTimeOffset? ParseDeviceTime(string value)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local));
        }
        return null;
    }

    private static string? NullIfEmpty(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 || trimmed.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private async Task<Dictionary<string, string?>> ListPackagesAsync(string options, List<string> warnings, CancellationToken ct)
    {
        var result = await _adb.ShellAsync($"pm list packages {options}", AdbClient.LongTimeout, ct).ConfigureAwait(false);
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!result.Ok && string.IsNullOrWhiteSpace(result.StdOut))
        {
            warnings.Add($"执行 pm list packages {options} 失败：{result.StdErr.Trim()}");
            return map;
        }

        foreach (var line in result.Lines)
        {
            var text = line.Trim();
            if (!text.StartsWith("package:", StringComparison.Ordinal)) continue;
            text = text[8..];

            string name = text;
            string? versionCode = null;
            var marker = text.IndexOf(" versionCode:", StringComparison.Ordinal);
            if (marker > 0)
            {
                name = text[..marker];
                versionCode = text[(marker + 13)..].Trim();
            }

            name = name.Trim();
            if (name.Length > 0)
            {
                map[name] = versionCode;
            }
        }

        return map;
    }

    private static Dictionary<string, string> ParseProperties(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in GetPropRegex().Matches(output))
        {
            map[match.Groups["k"].Value.Trim()] = match.Groups["v"].Value.Trim();
        }
        return map;
    }

    private static string? Get(Dictionary<string, string> props, string key) =>
        props.TryGetValue(key, out var value) ? value : null;

    private static (string Name, string Version) DetectRom(Dictionary<string, string> props)
    {
        var hyper = Get(props, "ro.mi.os.version.name") ?? Get(props, "ro.mi.os.version.incremental");
        if (!string.IsNullOrWhiteSpace(hyper)) return ("HyperOS", hyper!);

        var miui = Get(props, "ro.miui.ui.version.name");
        if (!string.IsNullOrWhiteSpace(miui)) return ("MIUI", miui!);

        var colorOs = Get(props, "ro.build.version.oplusrom") ?? Get(props, "ro.build.version.opporom");
        if (!string.IsNullOrWhiteSpace(colorOs)) return ("ColorOS / realme UI", colorOs!);

        var origin = Get(props, "ro.vivo.os.version");
        if (!string.IsNullOrWhiteSpace(origin)) return ("OriginOS", Get(props, "ro.vivo.os.build.display.id") ?? origin!);

        var magic = Get(props, "ro.build.version.magic");
        if (!string.IsNullOrWhiteSpace(magic)) return ("MagicOS", magic!);

        var emui = Get(props, "ro.build.version.emui");
        if (!string.IsNullOrWhiteSpace(emui)) return ("EMUI", emui!);

        var oneUi = Get(props, "ro.build.version.oneui");
        if (!string.IsNullOrWhiteSpace(oneUi)) return ("One UI", oneUi!);

        var flyme = Get(props, "ro.build.display.id");
        if (!string.IsNullOrWhiteSpace(flyme) && flyme!.Contains("Flyme", StringComparison.OrdinalIgnoreCase))
        {
            return ("Flyme", flyme);
        }

        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// 把品牌/厂商归一化成用于匹配名单的令牌。同一套系统的子品牌互相补充，
    /// 例如 Redmi 设备同时具备 xiaomi/redmi 令牌，这样小米系的规则也能命中。
    /// </summary>
    internal static IReadOnlyList<string> BuildBrandTokens(string? brand, string? manufacturer)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (var part in value.Split([' ', '-', '_', '/', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                tokens.Add(part.Trim().ToLowerInvariant());
            }
        }

        Add(brand);
        Add(manufacturer);

        var aliases = new (string Trigger, string[] Others)[]
        {
            ("xiaomi", ["xiaomi", "redmi", "poco", "blackshark"]),
            ("redmi", ["xiaomi", "redmi", "poco", "blackshark"]),
            ("poco", ["xiaomi", "redmi", "poco", "blackshark"]),
            ("blackshark", ["xiaomi", "redmi", "poco", "blackshark"]),
            ("vivo", ["vivo", "iqoo"]),
            ("iqoo", ["vivo", "iqoo"]),
            ("oppo", ["oppo", "oneplus", "realme"]),
            ("oneplus", ["oppo", "oneplus", "realme"]),
            ("realme", ["oppo", "oneplus", "realme"]),
            ("honor", ["honor", "hihonor"]),
            ("hihonor", ["honor", "hihonor"]),
        };

        foreach (var (trigger, others) in aliases)
        {
            if (tokens.Contains(trigger))
            {
                tokens.UnionWith(others);
            }
        }

        return tokens.ToList();
    }

    [GeneratedRegex(@"^\[(?<k>[^\]]+)\]:\s*\[(?<v>.*)\]\s*$", RegexOptions.Multiline)]
    private static partial Regex GetPropRegex();

    private sealed record PackageDetail(
        string Name,
        string? Installer,
        string? VersionName,
        string? CodePath,
        DateTimeOffset? FirstInstallTime,
        DateTimeOffset? LastUpdateTime,
        bool? NeverLaunched);
}
