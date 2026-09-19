using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using AndroidOptimize.App.Mvvm;
using AndroidOptimize.Core;
using AndroidOptimize.Core.Adb;
using AndroidOptimize.Core.Ai;
using AndroidOptimize.Core.Models;
using AndroidOptimize.Core.Services;
using AndroidOptimize.Core.Testing;

namespace AndroidOptimize.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SnapshotStore _snapshotStore;
    private readonly string _demoDirectory;
    private readonly RuleRepository _rules;
    private readonly AppSettings _settings;
    private readonly ClassificationCache _aiCache;
    private readonly RunLogger _log;
    private readonly List<PlanItemViewModel> _allPlanItems = [];

    private AdbClient? _adb;
    private DeviceScanner? _scanner;
    private DeviceInfo? _device;
    private PackageSnapshot? _snapshot;
    private OptimizationPlan? _plan;
    private ExecutionReport? _lastReport;
    private CancellationTokenSource? _cts;
    private string _scanFilter = string.Empty;
    private bool _showSystemApps;
    private SnapshotRowViewModel? _selectedSnapshot;

    public MainViewModel(bool demoMode = false)
    {
        AppPaths.EnsureCreated();
        IsDemoMode = demoMode;
        _demoDirectory = Path.Combine(Path.GetTempPath(), "AndroidOptimize-Demo");

        _log = new RunLogger();
        _log.LineWritten += AppendLog;

        _rules = RuleRepository.Load(AppContext.BaseDirectory);
        _settings = AppSettings.Load();
        _aiCache = ClassificationCache.Load();
        _snapshotStore = new SnapshotStore(demoMode ? _demoDirectory : null);

        // 命令必须先建好，后面的状态刷新会用到它们。
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => !IsBusy && _plan is { SelectedCount: > 0 });
        RollbackCommand = new AsyncRelayCommand(RollbackAsync, () => !IsBusy && SelectedSnapshot is not null);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true, onlySafe: false));
        SelectRecommendedCommand = new RelayCommand(() => SetAllSelected(true, onlySafe: true));
        SelectNoneCommand = new RelayCommand(() => SetAllSelected(false, onlySafe: false));
        ApplyLazyModeCommand = new RelayCommand(ApplyLazyMode);
        SelectUnknownCommand = new RelayCommand(SelectAllUnknown);
        ExportScanReportCommand = new RelayCommand(ExportScanReport, () => _snapshot is not null);
        ExportExecutionReportCommand = new RelayCommand(ExportExecutionReport, () => _lastReport is not null);
        OpenUserFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.UserRoot));
        OpenSnapshotFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.SnapshotsDir));
        SaveAiSettingsCommand = new RelayCommand(SaveAiSettings);
        ClearAiKeyCommand = new RelayCommand(ClearAiKey);
        ClearAiCacheCommand = new RelayCommand(ClearAiCache);
        SaveLogCommand = new RelayCommand(SaveLog);

        LocateAdb();
        ApplySettingsToUi();
        RefreshSnapshots();

        foreach (var warning in _rules.Warnings)
        {
            _log.Warn(warning);
        }
        _log.Info($"名单已加载：{_rules.SourceDescription}");
    }

    // ---------------- 命令 ----------------
    /// <summary>演示模式：连接的是虚拟设备，不会碰任何真机。</summary>
    public bool IsDemoMode { get; }

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand ExecuteCommand { get; }
    public AsyncRelayCommand RollbackCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectRecommendedCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand ApplyLazyModeCommand { get; }
    public RelayCommand SelectUnknownCommand { get; }
    public RelayCommand ExportScanReportCommand { get; }
    public RelayCommand ExportExecutionReportCommand { get; }
    public RelayCommand OpenUserFolderCommand { get; }
    public RelayCommand OpenSnapshotFolderCommand { get; }
    public RelayCommand SaveAiSettingsCommand { get; }
    public RelayCommand ClearAiKeyCommand { get; }
    public RelayCommand ClearAiCacheCommand { get; }
    public RelayCommand SaveLogCommand { get; }

    /// <summary>由界面注入的确认框，返回 true 表示用户同意继续。</summary>
    public Func<string, string, bool>? ConfirmHandler { get; set; }
    public Action<string, string>? MessageHandler { get; set; }
    public Action<int>? NavigateToTabHandler { get; set; }

    // ---------------- 集合 ----------------
    public ObservableCollection<PlanItemViewModel> PlanItems { get; } = [];
    public ObservableCollection<PackageRowViewModel> ScanRows { get; } = [];
    public ObservableCollection<ResultRowViewModel> ResultRows { get; } = [];
    public ObservableCollection<SnapshotRowViewModel> Snapshots { get; } = [];
    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    // ---------------- 状态 ----------------
    private string _statusText = "请先连接手机";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private string _deviceTitle = "未连接设备";
    public string DeviceTitle { get => _deviceTitle; private set => SetProperty(ref _deviceTitle, value); }

    private string _deviceSubtitle = "用数据线连接手机，并在手机上允许 USB 调试";
    public string DeviceSubtitle { get => _deviceSubtitle; private set => SetProperty(ref _deviceSubtitle, value); }

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; private set { if (SetProperty(ref _isConnected, value)) { OnPropertyChanged(nameof(ConnectionHint)); } } }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            RefreshCommands();
        }
    }

    public bool IsIdle => !IsBusy;

    private double _progressValue;
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }

    private bool _isProgressIndeterminate;
    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }

    private string _busyText = string.Empty;
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }

    private string _adbStatus = "正在检测 ADB…";
    public string AdbStatus { get => _adbStatus; private set => SetProperty(ref _adbStatus, value); }

    private bool _adbReady;
    public bool AdbReady { get => _adbReady; private set => SetProperty(ref _adbReady, value); }

    public string ConnectionHint
    {
        get
        {
            if (!AdbReady) return "缺少 ADB 工具，请确认程序目录下存在 bin\\adb.exe。";
            if (!IsConnected) return "连接步骤：开发者选项 → USB 调试（小米/红米还要打开「USB 调试（安全设置）」）→ 手机上点允许。";
            return "已连接。接下来点「开始扫描」，先看体检结果，再决定优化什么。";
        }
    }

    private string _planSummary = "还没有生成优化计划。";
    public string PlanSummary { get => _planSummary; private set => SetProperty(ref _planSummary, value); }

    private string _skippedSummary = string.Empty;
    public string SkippedSummary { get => _skippedSummary; private set => SetProperty(ref _skippedSummary, value); }

    private string _scanSummary = "还没有扫描。";
    public string ScanSummary { get => _scanSummary; private set => SetProperty(ref _scanSummary, value); }

    private string _resultSummary = "还没有执行过优化。";
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }

    private string _aftercareText = string.Empty;
    public string AftercareText { get => _aftercareText; private set => SetProperty(ref _aftercareText, value); }

    private bool _hasResults;
    public bool HasResults { get => _hasResults; private set => SetProperty(ref _hasResults, value); }

    public string ListVersionText => _rules.ListVersion;

    /// <summary>程序版本号，显示在标题栏，方便用户反馈问题时说明用的是哪一版。</summary>
    public string AppVersionText { get; } =
        "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    // ---------------- 选项 ----------------
    public bool IsTierScanOnly
    {
        get => _settings.LastTier == OptimizationTier.ScanOnly;
        set { if (value) SetTier(OptimizationTier.ScanOnly); }
    }

    public bool IsTierNormal
    {
        get => _settings.LastTier == OptimizationTier.Normal;
        set { if (value) SetTier(OptimizationTier.Normal); }
    }

    public bool IsTierGeek
    {
        get => _settings.LastTier == OptimizationTier.Geek;
        set { if (value) SetTier(OptimizationTier.Geek); }
    }

    public bool IsTierDanger
    {
        get => _settings.LastTier == OptimizationTier.Danger;
        set { if (value) SetTier(OptimizationTier.Danger); }
    }

    private string _tierDescription = string.Empty;
    public string TierDescription { get => _tierDescription; private set => SetProperty(ref _tierDescription, value); }

    public bool SafetyMode
    {
        get => _settings.SafetyMode;
        set => SetOption(() => _settings.SafetyMode, v => _settings.SafetyMode = v, value, RebuildPlan);
    }

    public bool DeepScan
    {
        get => _settings.DeepScan;
        set => SetOption(() => _settings.DeepScan, v => _settings.DeepScan = v, value);
    }

    public bool IncludeSettings
    {
        get => _settings.IncludeSettings;
        set => SetOption(() => _settings.IncludeSettings, v => _settings.IncludeSettings = v, value, RebuildPlan);
    }

    public bool AllowGuarded
    {
        get => _settings.AllowGuarded;
        set => SetOption(() => _settings.AllowGuarded, v => _settings.AllowGuarded = v, value, RebuildPlan);
    }

    /// <summary>关闭第三方应用的传感器权限（防摇一摇广告）。默认开启。</summary>
    public bool SensorPolicyEnabled
    {
        get => !_settings.DisabledPolicies.Contains("sensor_lock", StringComparer.OrdinalIgnoreCase);
        set
        {
            if (SensorPolicyEnabled == value) return;
            if (value)
            {
                _settings.DisabledPolicies.RemoveAll(p => p.Equals("sensor_lock", StringComparison.OrdinalIgnoreCase));
            }
            else if (!_settings.DisabledPolicies.Contains("sensor_lock", StringComparer.OrdinalIgnoreCase))
            {
                _settings.DisabledPolicies.Add("sensor_lock");
            }
            OnPropertyChanged();
            SaveSettingsQuietly();
            RebuildPlan();
        }
    }

    /// <summary>当前生效的策略集合 = 名单里的全部策略 − 用户关掉的。</summary>
    private IReadOnlySet<string> EnabledPolicyIds()
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in _rules.Policies)
        {
            if (_settings.DisabledPolicies.Contains(policy.Id, StringComparer.OrdinalIgnoreCase)) continue;
            enabled.Add(policy.Id);
        }
        return enabled;
    }

    public string AnimationScale
    {
        get => _settings.AnimationScale;
        set => SetOption(() => _settings.AnimationScale, v => _settings.AnimationScale = v, value, () =>
        {
            var item = _allPlanItems.FirstOrDefault(i => i.Model.Target == "global.animation_scale");
            if (item is not null) item.SelectedChoice = value;
        });
    }

    public bool EnableAi
    {
        get => _settings.EnableAi;
        set => SetOption(() => _settings.EnableAi, v => _settings.EnableAi = v, value, () =>
        {
            OnPropertyChanged(nameof(AiStatusText));
            OnPropertyChanged(nameof(AiKeyStatusText));
        });
    }

    public bool ConsentToSendPackageNames
    {
        get => _settings.ConsentToSendPackageNames;
        set => SetOption(() => _settings.ConsentToSendPackageNames, v => _settings.ConsentToSendPackageNames = v, value,
            () => OnPropertyChanged(nameof(AiStatusText)));
    }

    private string _apiKeyInput = string.Empty;
    public string ApiKeyInput { get => _apiKeyInput; set => SetProperty(ref _apiKeyInput, value); }

    public string AiKeyStatusText => _settings.HasApiKey
        ? $"已保存 API Key（{MaskKey(_settings.ApiKey)}）"
        : "未设置 API Key";

    public string AiStatusText
    {
        get
        {
            if (!_settings.EnableAi) return "AI 识别已关闭。未知应用只会被列出，不会产生建议。";
            if (!_settings.HasApiKey) return "已开启，但还缺少 API Key。";
            if (!_settings.ConsentToSendPackageNames) return "已开启，但尚未同意发送包名，AI 不会联网。";
            return $"已就绪。缓存了 {_aiCache.Count} 个应用的历史识别结果，命中缓存不会重复联网。";
        }
    }

    public string ScanFilter
    {
        get => _scanFilter;
        set { if (SetProperty(ref _scanFilter, value)) ApplyScanFilter(); }
    }

    public bool ShowSystemApps
    {
        get => _showSystemApps;
        set { if (SetProperty(ref _showSystemApps, value)) ApplyScanFilter(); }
    }

    public SnapshotRowViewModel? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set { if (SetProperty(ref _selectedSnapshot, value)) RefreshCommands(); }
    }

    // ---------------- 主流程 ----------------
    private void LocateAdb()
    {
        if (IsDemoMode)
        {
            var simulated = DemoDevice.Create();
            _adb = simulated;
            _scanner = new DeviceScanner(simulated);
            AdbReady = true;
            AdbStatus = "演示模式：使用虚拟手机，不会连接真机";
            _log.Warn("当前处于演示模式，所有操作都作用在虚拟设备上，不会修改任何真机。");
            return;
        }

        // 单文件发布时 adb 是内嵌的，先释放到用户数据目录。
        AdbBootstrap.Ensure(_log.Info);

        var location = AdbLocator.Locate(appBaseDirectory: AppContext.BaseDirectory);
        if (location is null)
        {
            AdbReady = false;
            AdbStatus = "未找到 adb.exe";
            _log.Error("未找到 adb.exe，请确认程序目录下存在 bin\\adb.exe。");
            return;
        }

        AdbReady = true;
        AdbStatus = $"ADB 就绪（{location.Source}）";
        _adb = new AdbClient(location.Path, trace: _log.Adb);
        _scanner = new DeviceScanner(_adb);
        _log.Info($"使用 ADB：{location.Path}（{location.Source}）");
    }

    public async Task ConnectAsync()
    {
        if (!AdbReady || _adb is null || _scanner is null)
        {
            MessageHandler?.Invoke("找不到 ADB", "程序目录下缺少 bin\\adb.exe，无法连接手机。请重新下载完整压缩包并解压后运行。");
            return;
        }

        if (IsDemoMode)
        {
            await RunBusyAsync("正在准备演示设备…", async ct =>
            {
                var info = await _scanner.GetDeviceInfoAsync(ct).ConfigureAwait(true);
                _device = info;
                IsConnected = true;
                DeviceTitle = info.DisplayName + "（演示）";
                DeviceSubtitle = info.SystemSummary;
                StatusText = "演示设备已就绪，可以开始扫描";
                _log.Success($"演示设备已就绪：{info.DisplayName}（{info.SystemSummary}）");
            }).ConfigureAwait(true);
            return;
        }

        await RunBusyAsync("正在连接手机…", async ct =>
        {
            _log.Step("开始连接设备。");
            var device = await _scanner.EnsureDeviceAsync(ct).ConfigureAwait(true);
            var client = _adb.ForDevice(device.Serial);
            var scanner = new DeviceScanner(client);
            var info = await scanner.GetDeviceInfoAsync(ct).ConfigureAwait(true);

            _adb = client;
            _scanner = scanner;
            _device = info;
            IsConnected = true;

            DeviceTitle = info.DisplayName;
            DeviceSubtitle = info.SystemSummary;
            StatusText = "已连接，可以开始扫描";
            _log.Success($"已连接：{info.DisplayName}（{info.SystemSummary}），序列号 {info.Serial}");
        }).ConfigureAwait(true);
    }

    public async Task ScanAsync()
    {
        if (!IsConnected) await ConnectAsync().ConfigureAwait(true);
        if (!IsConnected || _scanner is null || _device is null) return;

        await RunBusyAsync("正在扫描手机…", async ct =>
        {
            var progress = new Progress<string>(text => BusyText = text);
            _snapshot = await _scanner.ScanPackagesAsync(_device, DeepScan, progress, ct).ConfigureAwait(true);

            BuildScanRows();
            ScanSummary = $"共 {_snapshot.Packages.Values.Count(p => p.Installed)} 个已安装应用，" +
                          $"其中系统 {_snapshot.SystemCount} 个、第三方 {_snapshot.ThirdPartyCount} 个。" +
                          (_snapshot.DetailEnriched ? "已读取第三方应用的安装来源。" : string.Empty);
            StatusText = $"扫描完成：{ScanSummary}";
            _log.Success(ScanSummary);

            var suggestions = await ClassifyUnknownAsync(ct).ConfigureAwait(true);
            BuildPlan(suggestions);

            NavigateToTabHandler?.Invoke(1);
        }).ConfigureAwait(true);
    }

    private async Task<IReadOnlyList<PackageClassification>> ClassifyUnknownAsync(CancellationToken ct)
    {
        if (_snapshot is null) return [];

        var builder = new PlanBuilder();
        var unknown = builder.SelectUnknownTargets(_snapshot, _rules, _settings.AiMaxPackages);
        if (unknown.Count > 0)
        {
            _log.Info($"名单外未知应用 {unknown.Count} 个，将交给 AI 识别（可在设置中关闭）。");
        }

        var classifier = BuildClassifier();
        if (!classifier.IsAvailable)
        {
            if (unknown.Count > 0)
            {
                _log.Info("AI 识别未启用，未知应用不会产生建议。");
            }
            return [];
        }

        var progress = new Progress<string>(text => BusyText = text);
        var results = await classifier.ClassifyAsync(unknown, _snapshot.Device, progress, ct).ConfigureAwait(true);
        _log.Info($"AI 识别完成，取得 {results.Count} 条结果（其余为无法判定）。");
        OnPropertyChanged(nameof(AiStatusText));
        return results;
    }

    private IPackageClassifier BuildClassifier()
    {
        if (IsDemoMode)
        {
            return new DemoClassifier();
        }

        var classifier = new DeepSeekClassifier(_settings, _aiCache, _log);
        return classifier.IsAvailable ? classifier : new NoOpClassifier();
    }

    private void BuildPlan(IReadOnlyList<PackageClassification>? suggestions = null)
    {
        if (_snapshot is null || _device is null) return;

        var previous = _allPlanItems
            .Where(i => i.IsSelectable)
            .ToDictionary(i => i.Model.Key, i => (i.IsSelected, i.SelectedChoice), StringComparer.OrdinalIgnoreCase);

        var options = new PlanOptions
        {
            SafetyMode = _settings.SafetyMode,
            IncludeSettings = _settings.IncludeSettings,
            AllowGuarded = _settings.AllowGuarded,
            AnimationScaleChoice = _settings.AnimationScale,
            EnabledPolicies = EnabledPolicyIds(),
        };

        _plan = new PlanBuilder().Build(new PlanRequest
        {
            Device = _device,
            Snapshot = _snapshot,
            Rules = _rules,
            Tier = _settings.LastTier,
            Options = options,
            AiSuggestions = suggestions ?? [],
        });

        _allPlanItems.Clear();
        PlanItems.Clear();

        foreach (var item in _plan.Items)
        {
            var vm = new PlanItemViewModel(item);
            if (previous.TryGetValue(item.Key, out var old))
            {
                vm.IsSelected = old.IsSelected;
                if (item.Choices is { Count: > 0 } && old.SelectedChoice is not null)
                {
                    vm.SelectedChoice = old.SelectedChoice;
                }
            }
            vm.SelectionChanged += UpdatePlanSummary;
            _allPlanItems.Add(vm);
            PlanItems.Add(vm);
        }

        UpdatePlanSummary();
    }

    private void RebuildPlan()
    {
        if (_snapshot is null) return;
        var existingAi = _plan?.Items.Where(i => i.Kind == PlanItemKind.AiSuggestion)
            .Select(i => new PackageClassification
            {
                PackageName = i.Target,
                Category = i.Category,
                Action = i.Action,
                Confidence = i.Confidence,
                Reason = i.Reason ?? string.Empty,
            })
            .ToList() ?? [];
        BuildPlan(existingAi);
    }

    private void UpdatePlanSummary()
    {
        if (_plan is null)
        {
            PlanSummary = "还没有生成优化计划。";
            SkippedSummary = string.Empty;
            RefreshCommands();
            return;
        }

        // 这里的数字要跟着勾选状态走：应用懒人模式之后，「待确认项」应该变成 0。
        var pending = new List<string>();
        var aiPending = _plan.Items.Count(i => i.Kind == PlanItemKind.AiSuggestion && !i.IsSelected);
        var unknownPending = _plan.Items.Count(i => i.Kind == PlanItemKind.Unknown && !i.IsSelected);
        if (aiPending > 0) pending.Add($"AI 建议 {aiPending} 项");
        if (unknownPending > 0) pending.Add($"名单外的未知应用 {unknownPending} 项");

        var detail = $"共 {_plan.Items.Count} 项候选";
        if (pending.Count > 0)
        {
            detail += $"，其中 {string.Join("、", pending)}尚未勾选";
        }

        PlanSummary = $"已选择：{_plan.SelectionSummary}（{detail}）";
        var protectedCount = _plan.Skipped.Count(s => s.ProtectionLevel != ProtectionLevel.None);
        SkippedSummary = _plan.Skipped.Count == 0
            ? string.Empty
            : $"另有 {_plan.Skipped.Count} 项未列入计划，其中 {protectedCount} 项受保护名单保护。";
        RefreshCommands();
    }

    private void SetTier(OptimizationTier tier)
    {
        if (_settings.LastTier == tier) return;
        _settings.LastTier = tier;
        _settings.Save();

        OnPropertyChanged(nameof(IsTierScanOnly));
        OnPropertyChanged(nameof(IsTierNormal));
        OnPropertyChanged(nameof(IsTierGeek));
        OnPropertyChanged(nameof(IsTierDanger));
        UpdateTierDescription();
        RebuildPlan();
    }

    private void UpdateTierDescription()
    {
        TierDescription = _settings.LastTier switch
        {
            OptimizationTier.ScanOnly => "只体检，不修改任何东西。适合先看看手机里有什么。",
            OptimizationTier.Normal => "推荐。清理系统广告、遥测统计、快应用与应用商店，保留反诈、支付、无障碍等关键服务。",
            OptimizationTier.Geek => "在一般优化的基础上，额外提供云服务、互联互通、语音助手等可选项目，每一项都能单独勾选。",
            OptimizationTier.Danger => "包含高风险与证据不足的项目，需要你逐项确认。不了解的项目请保持不勾选。",
            _ => string.Empty,
        };
    }

    public async Task ExecuteAsync()
    {
        if (_plan is null || _plan.SelectedCount == 0)
        {
            MessageHandler?.Invoke("没有可执行的项目", "请先扫描手机，并在「优化计划」里勾选要处理的项目。");
            return;
        }

        if (_adb is null) return;

        var summary = _plan.SelectionSummary;
        var unknownSelected = _plan.SelectedItems.Count(i => i.Kind == PlanItemKind.Unknown);
        var unknownWarning = unknownSelected == 0
            ? string.Empty
            : $"\n\n注意：其中 {unknownSelected} 个是「名单里查不到的未知应用」。" +
              "\n程序无法判断它们是什么，请确认列表里没有要紧的应用" +
              "（医院挂号、社区服务、子女装的软件等）。\n" +
              "它们会被停用，随时可以在「回滚」页还原。";

        var confirmed = ConfirmHandler?.Invoke(
            "确认开始优化",
            $"即将对「{_device?.DisplayName}」执行：\n\n{summary}\n\n" +
            "执行前会自动保存可回滚的快照，随时可以在「回滚」页还原。" + unknownWarning +
            "\n\n是否继续？") ?? false;
        if (!confirmed) return;

        await RunBusyAsync("正在执行优化…", async ct =>
        {
            var executor = new OptimizationExecutor(_adb, _snapshotStore, _log);
            var progress = new Progress<ExecutionProgress>(p =>
            {
                BusyText = p.Message;
                if (p.Total > 0) ProgressValue = p.Index * 100.0 / p.Total;
            });

            _lastReport = await executor.ExecuteAsync(_plan, progress, ct).ConfigureAwait(true);
            BuildResultRows(_lastReport);
            HasResults = true;
            ResultSummary = $"{_lastReport.Summary}（耗时 {_lastReport.Duration.TotalSeconds:0.0} 秒）";
            StatusText = "优化完成";

            AftercareText = BuildAftercare(_lastReport);
            RefreshSnapshots();

            var reportPath = ReportWriter.WriteExecutionReport(
                _lastReport, _plan,
                IsDemoMode ? Path.Combine(_demoDirectory, $"演示-执行报告-{DateTime.Now:HHmmss}.md") : null);
            _log.Success($"执行报告已保存：{reportPath}");
            NavigateToTabHandler?.Invoke(2);
        }).ConfigureAwait(true);
    }

    private static string BuildAftercare(ExecutionReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("上面这些已经由程序通过 ADB 完成，不需要你再进手机设置里操作。");
        text.AppendLine();
        text.AppendLine("· 建议重启一次手机，让被停用的组件彻底退出后台；");
        text.AppendLine("· 重启后可以再扫描一次，确认该安静的都安静了；");
        text.AppendLine("· 如果哪个应用不该被处理，在「回滚」页一键还原即可。");
        if (report.FailedCount > 0)
        {
            text.AppendLine();
            text.AppendLine($"另外有 {report.FailedCount} 项执行失败，多数是系统不支持该限制导致，可以在日志页查看具体原因。");
        }
        return text.ToString();
    }

    private async Task RollbackAsync()
    {
        if (_adb is null || SelectedSnapshot is null)
        {
            MessageHandler?.Invoke("请先选择快照", "在下方列表里选择要还原的那一次优化记录。");
            return;
        }

        var confirmed = ConfirmHandler?.Invoke(
            "确认还原",
            $"即将把「{_device?.DisplayName}」还原到 {SelectedSnapshot.Summary.Snapshot.CreatedAt:yyyy-MM-dd HH:mm} 之前的状态。\n\n" +
            $"共 {SelectedSnapshot.Summary.Snapshot.Entries.Count} 项将被还原。\n\n是否继续？") ?? false;
        if (!confirmed) return;

        await RunBusyAsync("正在回滚…", async ct =>
        {
            var service = new RollbackService(_adb, _log);
            var progress = new Progress<ExecutionProgress>(p =>
            {
                BusyText = p.Message;
                if (p.Total > 0) ProgressValue = p.Index * 100.0 / p.Total;
            });

            var report = await service.RestoreAsync(
                SelectedSnapshot.Summary.Snapshot, SelectedSnapshot.Summary.Path, progress, ct).ConfigureAwait(true);

            var text = new StringBuilder();
            text.AppendLine($"还原完成：成功 {report.SuccessCount} 项，失败 {report.FailedCount} 项。");
            foreach (var failure in report.Results.Where(r => !r.Success).Take(10))
            {
                text.AppendLine($"· {failure.DisplayName}：{failure.Message}");
            }
            text.AppendLine();
            text.AppendLine("建议重启手机让还原完全生效，然后重新扫描一次。");

            MessageHandler?.Invoke("还原结果", text.ToString());
            StatusText = $"还原完成（成功 {report.SuccessCount} 项）";
        }).ConfigureAwait(true);
    }

    // ---------------- 界面数据 ----------------
    private void BuildScanRows()
    {
        ScanRows.Clear();
        if (_snapshot is null) return;

        foreach (var entry in _snapshot.Packages.Values.Where(p => p.Installed))
        {
            var rule = _rules.FindPackageRule(entry.Name, _snapshot.Device.BrandTokens);
            var protection = _rules.MatchProtection(entry.Name);

            var listState = protection.IsProtected
                ? $"受保护（{protection.Pattern.Category}）"
                : rule is null ? "名单外" : $"名单：{rule.DisplayName}";
            var note = protection.IsProtected
                ? protection.Pattern.Reason ?? string.Empty
                : rule?.Reason ?? (entry.IsThirdParty ? "名单里没有记录，可交给 AI 识别。" : "系统组件，名单未收录。");

            ScanRows.Add(new PackageRowViewModel
            {
                PackageName = entry.Name,
                DisplayName = rule?.DisplayName ?? string.Empty,
                TypeText = entry.IsThirdParty ? "第三方" : "系统",
                StateText = entry.Disabled ? "停用" : "启用",
                InstallerText = string.IsNullOrWhiteSpace(entry.Installer) ? "-" : entry.Installer!,
                FirstInstallText = entry.FirstInstallTime is null ? "-" : entry.FirstInstallTime.Value.ToString("yyyy-MM-dd"),
                ListStateText = listState,
                NoteText = note,
                IsThirdParty = entry.IsThirdParty,
            });
        }

        ApplyScanFilter();
    }

    private void ApplyScanFilter()
    {
        var filter = _scanFilter.Trim().ToLowerInvariant();
        var rows = ScanRows.Where(r =>
            (_showSystemApps || r.IsThirdParty || r.ListStateText.StartsWith("名单") || r.ListStateText.StartsWith("受保护")) &&
            (filter.Length == 0 || r.SearchText.Contains(filter)));

        var ordered = rows
            .OrderByDescending(r => r.IsThirdParty)
            .ThenBy(r => r.ListStateText.StartsWith("受保护") ? 0 : 1)
            .ThenBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ScanRowsView.Clear();
        foreach (var row in ordered) ScanRowsView.Add(row);
        OnPropertyChanged(nameof(ScanRowsView));
    }

    public ObservableCollection<PackageRowViewModel> ScanRowsView { get; } = [];

    private void BuildResultRows(ExecutionReport report)
    {
        ResultRows.Clear();
        var index = 0;
        foreach (var result in report.Results)
        {
            index++;
            var verify = result.Status is ActionStatus.Failed or ActionStatus.Skipped
                ? "-"
                : result.Verified ? "通过" : "未确认";

            ResultRows.Add(new ResultRowViewModel
            {
                Index = index,
                DisplayName = result.Item.DisplayName,
                Target = result.Item.Target,
                ActionText = result.Item.ActionText,
                StatusText = result.StatusText,
                VerifyText = verify,
                Status = result.Status,
                Message = result.Message,
                Hint = result.Hint,
            });
        }
    }

    public void RefreshSnapshots()
    {
        Snapshots.Clear();
        foreach (var summary in _snapshotStore.List())
        {
            Snapshots.Add(new SnapshotRowViewModel { Summary = summary });
        }
        SelectedSnapshot = Snapshots.FirstOrDefault();
        RefreshCommands();
    }

    private void SetAllSelected(bool selected, bool onlySafe)
    {
        foreach (var item in _allPlanItems.Where(i => i.IsSelectable))
        {
            if (!selected)
            {
                item.IsSelected = false;
                continue;
            }

            item.IsSelected = onlySafe
                ? item.Kind != PlanItemKind.AiSuggestion
                  && item.Kind != PlanItemKind.Unknown
                  && item.Risk != RiskLevel.High
                : true;
        }
        UpdatePlanSummary();
    }

    /// <summary>
    /// 懒人模式：不做任何猜测，只保留「必须保留清单」里的应用，其余全部勾选处理。
    /// 判断依据完全是名单里那份白名单（用户可以自己改），程序不猜。
    /// </summary>
    private void ApplyLazyMode()
    {
        if (_allPlanItems.Count == 0)
        {
            MessageHandler?.Invoke("还没有优化计划",
                "请先点「开始扫描手机」，程序才知道这台手机上有哪些应用。");
            return;
        }

        if (_plan is null) return;

        var kept = PlanBuilder.ApplyLazyMode(_plan, _rules);
        // IsSelected 是直接改在 Model 上的，这里把界面通知补上。
        foreach (var item in _allPlanItems) item.RaiseIsSelectedChanged();
        UpdatePlanSummary();

        var selected = _plan.SelectedCount;
        var keptPreview = kept.Count == 0 ? "（无）" : string.Join("、", kept.Take(8)) + (kept.Count > 8 ? " 等" : string.Empty);
        MessageHandler?.Invoke("已应用懒人模式",
            $"已勾选 {selected} 项——「必须保留清单」之外的全部处理。\n\n" +
            $"保留的 {kept.Count} 项：{keptPreview}\n\n" +
            "⚠ 请务必扫一眼下面的列表再执行，把要紧的应用取消勾选\n" +
            "（比如医院挂号、社区服务、子女给你装的软件）。\n\n" +
            "处理方式是「停用」：应用不能再运行、图标会消失、不会再弹广告，" +
            "效果和卸载一样，随时可以在「回滚」页一键还原。\n\n" +
            "如果发现清单里少写了哪个应用，可以在「回滚」页还原后，" +
            "把包名加进 data/packages.json 的 lazyMode.keep 再试。");
    }

    /// <summary>一键选中全部名单外的应用（更激进，需要自己核对列表）。</summary>
    private void SelectAllUnknown()
    {
        var unknown = _allPlanItems.Where(i => i.Kind == PlanItemKind.Unknown).ToList();
        if (unknown.Count == 0)
        {
            MessageHandler?.Invoke("没有未知应用",
                "这台手机上没有「名单外」的第三方应用。\n\n" +
                "如果确实装了很多应用，可以点「开始扫描手机」重新扫描一次。");
            return;
        }

        foreach (var item in unknown)
        {
            item.IsSelected = true;
        }
        UpdatePlanSummary();

        var neverLaunched = unknown.Count(i => i.Model.NeverLaunched == true);
        MessageHandler?.Invoke("已选中全部未知应用",
            $"已勾选 {unknown.Count} 个「名单里查不到」的应用，其中 {neverLaunched} 个是从未打开过的。\n\n" +
            "它们会被**停用**（不是卸载），随时可以在「回滚」页一键还原。\n\n" +
            "⚠ 请务必扫一眼列表再执行，把要紧的应用取消勾选——\n" +
            "比如医院挂号、社区服务、子女给你装的软件。");
    }

    // ---------------- 导出与工具 ----------------
    private void ExportScanReport()
    {
        if (_snapshot is null) return;
        var path = ReportWriter.WriteScanReport(
            _snapshot, _rules,
            IsDemoMode ? Path.Combine(_demoDirectory, $"演示-体检报告-{DateTime.Now:HHmmss}.md") : null);
        MessageHandler?.Invoke("体检报告已导出", path);
        OpenFolder(Path.GetDirectoryName(path)!);
    }

    private void ExportExecutionReport()
    {
        if (_lastReport is null || _plan is null) return;
        var path = ReportWriter.WriteExecutionReport(
            _lastReport, _plan,
            IsDemoMode ? Path.Combine(_demoDirectory, $"演示-执行报告-{DateTime.Now:HHmmss}.md") : null);
        MessageHandler?.Invoke("执行报告已导出", path);
        OpenFolder(Path.GetDirectoryName(path)!);
    }

    private void SaveAiSettings()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyInput))
        {
            _settings.ApiKey = ApiKeyInput;
            _settings.Save();
            _log.Info("已保存新的 DeepSeek API Key（DPAPI 加密存储）。");
            ApiKeyInput = string.Empty;
            OnPropertyChanged(nameof(AiKeyStatusText));
            OnPropertyChanged(nameof(AiStatusText));
        }
        MessageHandler?.Invoke("AI 设置", "设置已保存。\n\nAPI Key 使用 Windows 凭据加密后保存在本机，不会写入程序目录，也不会上传到任何服务器。");
    }

    private void ClearAiKey()
    {
        _settings.ApiKey = null;
        _settings.Save();
        OnPropertyChanged(nameof(AiKeyStatusText));
        OnPropertyChanged(nameof(AiStatusText));
        _log.Info("已清除本机保存的 API Key。");
    }

    private void ClearAiCache()
    {
        try
        {
            if (File.Exists(AppPaths.AiCacheFile)) File.Delete(AppPaths.AiCacheFile);
            _log.Info("已清空 AI 识别缓存。");
            MessageHandler?.Invoke("AI 缓存", "缓存已清空，下次扫描会重新识别未知应用。");
        }
        catch (Exception ex)
        {
            _log.Warn($"清空缓存失败：{ex.Message}");
        }
    }

    private void SaveLog()
    {
        MessageHandler?.Invoke("日志文件", _log.Path);
        OpenFolder(AppPaths.LogsDir);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打开资源管理器失败不影响主流程。
        }
    }

    private void ApplySettingsToUi()
    {
        UpdateTierDescription();
        OnPropertyChanged(nameof(SafetyMode));
        OnPropertyChanged(nameof(DeepScan));
        OnPropertyChanged(nameof(IncludeSettings));
        OnPropertyChanged(nameof(AllowGuarded));
        OnPropertyChanged(nameof(SensorPolicyEnabled));
        OnPropertyChanged(nameof(AnimationScale));
        OnPropertyChanged(nameof(EnableAi));
        OnPropertyChanged(nameof(ConsentToSendPackageNames));
        OnPropertyChanged(nameof(AiStatusText));
        OnPropertyChanged(nameof(AiKeyStatusText));
    }

    private static string MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        return key.Length <= 8 ? "****" : $"{key[..4]}****{key[^4..]}";
    }

    /// <summary>设置项统一走这里：值没变就不做事，变了就落盘并通知界面。</summary>
    private void SetOption<T>(Func<T> getter, Action<T> setter, T value, Action? after = null, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(getter(), value)) return;
        setter(value);
        SaveSettingsQuietly();
        if (name is not null) OnPropertyChanged(name);
        after?.Invoke();
    }

    private void SaveSettingsQuietly()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            _log.Warn($"保存设置失败：{ex.Message}");
        }
    }

    private void AppendLog(string line)
    {
        var level = line.Contains("[错误]") ? "错误"
            : line.Contains("[成功]") ? "成功"
            : line.Contains("[提示]") ? "提示"
            : "信息";

        void Add()
        {
            LogEntries.Add(new LogEntry { Text = line, Level = level });
            while (LogEntries.Count > 2000) LogEntries.RemoveAt(0);
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Add();
        else dispatcher.BeginInvoke(Add);
    }

    private async Task RunBusyAsync(string title, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;

        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        BusyText = title;
        StatusText = title;
        _cts = new CancellationTokenSource();

        try
        {
            await work(_cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            _log.Warn("操作被用户取消。");
        }
        catch (DeviceException ex)
        {
            StatusText = ex.Message;
            _log.Error($"{ex.Message}\n{ex.Hint}");
            MessageHandler?.Invoke(ex.Message, ex.Hint);
        }
        catch (AdbException ex)
        {
            StatusText = "ADB 执行失败";
            _log.Error(ex.Message);
            MessageHandler?.Invoke("ADB 执行失败", ex.Message);
        }
        catch (Exception ex)
        {
            StatusText = "出现异常";
            _log.Error(ex.ToString());
            MessageHandler?.Invoke("出现异常", $"{ex.Message}\n\n详细信息已写入日志：{_log.Path}");
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            BusyText = string.Empty;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        _log.Warn("用户请求取消当前操作。");
    }

    private void RefreshCommands()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        ExecuteCommand.RaiseCanExecuteChanged();
        RollbackCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ExportScanReportCommand.RaiseCanExecuteChanged();
        ExportExecutionReportCommand.RaiseCanExecuteChanged();
    }

    public string AboutText =>
        $"名单版本 {_rules.ListVersion} · 应用规则 {_rules.PackageRuleCount} 条 · 保护规则 {_rules.ProtectionRuleCount} 条\n" +
        $"配置文件来源：{_rules.SourceDescription}";

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _log.LineWritten -= AppendLog;
        _log.Dispose();
    }
}
