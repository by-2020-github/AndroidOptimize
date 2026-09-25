using System.Globalization;
using System.Text.RegularExpressions;
using AndroidOptimize.Core.Adb;
using AndroidOptimize.Core.Apk;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Services;

public sealed class DeviceException : Exception
{
    public DeviceException(string message, string hint) : base(message) => Hint = hint;
    public string Hint { get; }
}

public sealed partial class DeviceScanner
{
    /// <summary>
    /// 最多逐个 dumpsys 多少个应用。真机上单个 dumpsys 约 100 毫秒，4 路并发下 400 个约 10 秒，
    /// 完全可以接受——这个上限只是防止极端设备上无休止地跑下去。
    ///
    /// 注意：早期这里是 150，结果「第三方应用 + 名册收录的系统应用」一超过 150 就整段跳过，
    /// 装得多一点的手机（很常见）就会既没有安装来源、也没有权限。别再用这种「一个数字卡掉整层功能」的写法。
    /// </summary>
    private const int DetailScanLimit = 400;

    /// <summary>
    /// 单次扫描最多读多少个应用名。读名字要分几次取 APK 的局部字节，比 dumpsys 慢得多，
    /// 所以单独设一个更小的批次；结果会进缓存，超过上限时是**分批读**而不是不读，
    /// 再扫一次就把剩下的补上，最终所有应用都会有名字。
    /// </summary>
    private const int LabelReadPerScan = 250;

    private readonly AdbClient _adb;
    private readonly AppLabelCache _labelCache = AppLabelCache.Load();
    private readonly AppDetailCache _detailCache;
    private readonly ApkLabelSource? _labelSource;
    private readonly Action<string>? _log;

    /// <summary>演示模式（虚拟设备）不要写这份缓存：假数据的包名和版本可能和真机撞上。</summary>
    private readonly bool _useDetailCache;

    /// <summary>labelSource 为空时用默认实现（从真机 APK 里读应用名）。</summary>
    private readonly AppCatalog? _catalog;

    public DeviceScanner(
        AdbClient adb,
        ApkLabelSource? labelSource = null,
        AppCatalog? catalog = null,
        Action<string>? log = null,
        bool useDetailCache = true,
        string? detailCachePath = null)
    {
        _adb = adb;
        _labelSource = labelSource;
        _catalog = catalog;
        _log = log;
        _useDetailCache = useDetailCache;
        _detailCache = AppDetailCache.Load(detailCachePath);
    }

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
        bool forceRefreshDetails = false,
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
                .Where(p => p.Installed && NeedsDetailScan(p))
                .Select(p => p.Name)
                .ToList();

            if (targets.Count == 0)
            {
                progress?.Report("没有需要深度扫描的应用。");
            }
            else if (targets.Count > DetailScanLimit)
            {
                warnings.Add($"需要逐个读取的应用过多（{targets.Count} 个，超过 {DetailScanLimit} 个），" +
                             "已跳过安装来源与权限读取。这属于极端情况，请把日志发给开发者。");
            }
            else
            {
                // 权限与安装来源默认走缓存：这些字段只跟「包名 + 版本号」有关（申请了什么权限写在 APK 里），
                // 应用不更新就不会变。勾了「强制重新读取权限」或者缓存里没有的，才真的去跑 dumpsys。
                var pending = new List<string>();
                var cacheHits = 0;
                foreach (var name in targets)
                {
                    var cached = _useDetailCache && !forceRefreshDetails
                        ? _detailCache.Get(name, map[name].VersionName)
                        : null;
                    if (cached is null)
                    {
                        pending.Add(name);
                        continue;
                    }

                    map[name] = cached.ApplyTo(map[name]);
                    cacheHits++;
                }

                if (pending.Count == 0)
                {
                    progress?.Report($"安装来源与权限：{cacheHits} 个全部命中缓存。");
                }
                else
                {
                    progress?.Report(forceRefreshDetails
                        ? $"强制重新读取 {pending.Count} 个应用的安装来源与权限（忽略缓存）…"
                        : $"安装来源与权限：命中缓存 {cacheHits} 个，读取 {pending.Count} 个…");
                }

                var details = await EnrichAsync(pending, progress, ct).ConfigureAwait(false);
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
                            HasLauncher = detail.HasLauncher,
                            Permissions = detail.Permissions,
                        };

                        if (_useDetailCache)
                        {
                            _detailCache.Set(name, existing.VersionName, CachedPackageDetail.From(map[name]));
                        }
                    }
                }
                _detailCache.Save();
                enriched = true;
                _log?.Invoke($"安装来源与权限：命中缓存 {cacheHits} 个，读取 {pending.Count} 个。");

                // 再读一遍应用显示名：名单里没有的应用只剩包名，用户根本不知道那是什么。
                // 只读 APK 的局部字节，而且有缓存，第二次扫描几乎不花时间。
                // 目标是「第三方应用 + 名册收录的系统组件」——正好是需要逐个看 dumpsys 的那批，
                // 不再为几百个纯内部系统组件白跑一遍。
                await ReadLabelsAsync(map, targets, progress, ct)
                    .ConfigureAwait(false);
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

    /// <summary>
    /// 哪些包需要逐个读 dumpsys：第三方应用（要看安装来源），
    /// 以及名册里有清理结论的系统应用（要判断它有没有桌面图标，决定能不能自动勾选）。
    /// </summary>
    private bool NeedsDetailScan(PackageEntry entry)
    {
        if (entry.IsThirdParty) return true;
        if (_catalog is null) return false;

        var catalogEntry = _catalog.Find(entry.Name);
        return catalogEntry is not null && RemovalAdvice.IsActionable(catalogEntry.Removal, OptimizationTier.Geek);
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

    /// <summary>读第三方应用的显示名（APK 里的 label）。读不到就保持 null，界面回退到包名。</summary>
    private async Task ReadLabelsAsync(
        Dictionary<string, PackageEntry> map,
        IReadOnlyList<string> targets,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var missing = new List<string>();
        var cacheHits = 0;
        var knownUnreadable = 0;
        foreach (var name in targets)
        {
            if (!map.TryGetValue(name, out var entry)) continue;

            var cached = _labelCache.Get(name, entry.VersionName);
            if (cached is not null)
            {
                map[name] = entry with { Label = cached };
                cacheHits++;
            }
            else if (_labelCache.IsKnownUnreadable(name, entry.VersionName))
            {
                // 以前就读不出来（RRO overlay、系统内部组件本来就没有应用名），不再重试
                knownUnreadable++;
            }
            else missing.Add(name);
        }

        if (missing.Count == 0)
        {
            progress?.Report($"应用名已全部命中缓存（{targets.Count} 个，其中 {knownUnreadable} 个上次就读不出来）。");
            _log?.Invoke($"应用名：{targets.Count} 个全部命中缓存（含 {knownUnreadable} 个已知读不出），没有读 APK。");
            return;
        }

        // 一次读不完就分批发：读过的会进缓存，下次扫描接着读剩下的，最终全部有名字。
        if (missing.Count > LabelReadPerScan)
        {
            progress?.Report($"需要读取名字的应用有 {missing.Count} 个，本次先读 {LabelReadPerScan} 个" +
                             "（读过的会缓存，再扫描一次会把剩下的补上）…");
            missing = missing.Take(LabelReadPerScan).ToList();
        }

        progress?.Report($"正在读取 {missing.Count} 个应用的显示名…（首次较慢，之后走缓存）");

        var source = _labelSource ?? new ApkLabelSource(_adb, _log);
        var completed = 0;
        var succeeded = 0;
        using var gate = new SemaphoreSlim(3);

        var tasks = missing.Select(async name =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var label = await source.ReadLabelAsync(name, ct).ConfigureAwait(false);
                lock (map)
                {
                    if (map.TryGetValue(name, out var entry))
                    {
                        if (label is not null)
                        {
                            map[name] = entry with { Label = label };
                            _labelCache.Set(name, entry.VersionName, label);
                            succeeded++;
                        }
                        else
                        {
                            // 把「读不出来」也记下来，下次扫描不再花时间重试
                            _labelCache.SetUnreadable(name, entry.VersionName);
                        }
                    }
                    completed++;
                }

                if (completed % 5 == 0 || completed == missing.Count)
                {
                    progress?.Report($"正在读取应用名… {completed}/{missing.Count}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单个应用读不到名字不影响整体扫描。
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        _labelCache.Save();

        _log?.Invoke($"应用名：命中缓存 {cacheHits} 个，已知读不出 {knownUnreadable} 个，本次读取 {missing.Count} 个，" +
                     $"成功 {succeeded} 个、读不出 {missing.Count - succeeded} 个（读不出的也会记住，下次不再重试）。");
    }

    private static PackageDetail ParsePackageDetail(string packageName, string dump)
    {
        string? installer = null, versionName = null, codePath = null;
        DateTimeOffset? firstInstall = null, lastUpdate = null;
        bool? neverLaunched = null;
        var hasLauncher = false;

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
            else if (line.Contains("android.intent.category.LAUNCHER", StringComparison.Ordinal))
            {
                // dumpsys 里出现 LAUNCHER 类别，说明这个应用在桌面上有图标。
                hasLauncher = true;
            }
        }

        return new PackageDetail(packageName, installer, versionName, codePath, firstInstall, lastUpdate,
            neverLaunched, hasLauncher, ParseRequestedPermissions(dump));
    }

    /// <summary>
    /// 从 dumpsys package 的输出里取「requested permissions」段。
    ///
    /// 各家 ROM 的排版不完全一样，所以这里不去匹配固定行号，而是：
    /// 找到段头 → 之后缩进更深的、长得像权限名（带点、没有空格）的行都算一条，
    /// 遇到缩进回到段头同级、或者出现不像权限名的行（例如 install permissions:）就结束。
    /// 这样 Android 9 的纯权限名列表和 Android 13+ 的「权限名: granted=true」两种格式都能吃下。
    ///
    /// 段头不存在时返回 null（= 没读到），不能返回空列表——那会被当成「这个应用没申请权限」。
    /// </summary>
    internal static IReadOnlyList<string>? ParseRequestedPermissions(string dump)
    {
        var permissions = new List<string>();
        var found = false;
        var headerIndent = 0;

        foreach (var rawLine in dump.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (!found)
            {
                if (trimmed.Equals("requested permissions:", StringComparison.Ordinal))
                {
                    found = true;
                    headerIndent = CountIndent(line);
                }
                continue;
            }

            if (trimmed.Length == 0) continue;

            // 缩进回到段头同级（install permissions: / declared permissions: …）→ 这一段结束
            if (CountIndent(line) <= headerIndent) break;

            // 下一段的段头。即使缩进比段头深，也不能当成权限
            if (trimmed.EndsWith(':')) break;

            // 「android.permission.SYSTEM_ALERT_WINDOW: granted=true」→ 取冒号前的部分
            var name = trimmed;
            var colon = name.IndexOf(':');
            if (colon > 0) name = name[..colon];
            name = name.Trim();

            // 这一段里偶尔会夹进不像权限名的东西（例如 MediaStore.Images.Media.EXTERNAL_CONTENT_URI），
            // 也有厂商把权限名拼错成「ACCESS_WIFI_ STATE」这种带空格的。
            // 这些都不该让解析提前结束——只跳过，继续往下读，直到真正进入下一段。
            if (name.Length == 0 || !name.Contains('.')) continue;

            permissions.Add(name);
        }

        return found ? permissions : null;
    }

    private static int CountIndent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ') count++;
        return count;
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
        bool? NeverLaunched,
        bool HasLauncher,
        IReadOnlyList<string>? Permissions);
}
