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
    /// <summary>连上手机后会换成这台手机自己的目录（devices\&lt;序列号&gt;\snapshots）。</summary>
    private SnapshotStore _snapshotStore;
    private readonly string _demoDirectory;
    private readonly RuleRepository _rules;
    private readonly AppCatalog _catalog;
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
    /// <summary>AI 对名单外应用的识别结果，按包名索引。用于「建议」列和计划里的 AI 建议项。</summary>
    private IReadOnlyDictionary<string, PackageClassification> _aiResults =
        new Dictionary<string, PackageClassification>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private string _scanFilter = string.Empty;
    private SnapshotRowViewModel? _selectedSnapshot;

    public MainViewModel(bool demoMode = false)
    {
        AppPaths.EnsureCreated();
        IsDemoMode = demoMode;
        _demoDirectory = Path.Combine(Path.GetTempPath(), "AndroidOptimize-Demo");

        _log = new RunLogger();
        _log.LineWritten += AppendLog;

        _rules = RuleRepository.Load(AppContext.BaseDirectory);
        _catalog = AppCatalog.Load(AppContext.BaseDirectory);
        _settings = AppSettings.Load();
        _aiCache = ClassificationCache.Load();
        _snapshotStore = new SnapshotStore(demoMode ? _demoDirectory : null);

        // 命令必须先建好，后面的状态刷新会用到它们。
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        ScanCommand = new AsyncRelayCommand(() => ScanAsync(), () => !IsBusy);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => !IsBusy && _plan is { SelectedCount: > 0 });
        ClassifyNowCommand = new AsyncRelayCommand(ClassifyNowAsync, () => !IsBusy && CanClassifyNow);
        RollbackCommand = new AsyncRelayCommand(RollbackAsync, () => !IsBusy && SelectedSnapshot is not null);
        ShowRollbackDetailCommand = new AsyncRelayCommand(LoadRollbackDetailAsync, () => SelectedSnapshot is not null);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true, onlySafe: false));
        SelectRecommendedCommand = new RelayCommand(() => SetAllSelected(true, onlySafe: true));
        SelectNoneCommand = new RelayCommand(() => SetAllSelected(false, onlySafe: false));
        SelectUnknownCommand = new RelayCommand(SelectAllUnknown);
        SetSelectedDisableCommand = new AsyncRelayCommand(() => ExecuteSelectedActionAsync(PackageAction.Disable), () => !IsBusy);
        SetSelectedUninstallCommand = new AsyncRelayCommand(() => ExecuteSelectedActionAsync(PackageAction.Uninstall), () => !IsBusy);
        ExportScanReportCommand = new RelayCommand(ExportScanReport, () => _snapshot is not null);
        ExportExecutionReportCommand = new RelayCommand(ExportExecutionReport, () => _lastReport is not null);
        OpenUserFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.UserRoot));
        OpenSnapshotFolderCommand = new RelayCommand(OpenSnapshotFolder);
        OpenDeviceFolderCommand = new RelayCommand(OpenDeviceFolder, () => _device is not null);
        ExportStatisticsCommand = new RelayCommand(ExportStatistics);
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
        _log.Info($"离线名册已加载：{_catalog.Count} 条（{_catalog.Source}，生成于 {_catalog.GeneratedAt}）");
    }

    // ---------------- 命令 ----------------
    /// <summary>演示模式：连接的是虚拟设备，不会碰任何真机。</summary>
    public bool IsDemoMode { get; }

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand ExecuteCommand { get; }
    /// <summary>对当前扫描结果立即做一次 AI 识别，不需要重新扫描。</summary>
    public AsyncRelayCommand ClassifyNowCommand { get; }
    public AsyncRelayCommand RollbackCommand { get; }
    /// <summary>查看选中的快照里到底改了哪些东西（双击列表也是这个动作）。</summary>
    public AsyncRelayCommand ShowRollbackDetailCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectRecommendedCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand SelectUnknownCommand { get; }
    /// <summary>把勾选的应用**立即停用**（弹窗确认后执行）</summary>
    public AsyncRelayCommand SetSelectedDisableCommand { get; }
    /// <summary>把勾选的应用**立即卸载**（弹窗确认后执行）</summary>
    public AsyncRelayCommand SetSelectedUninstallCommand { get; }
    public RelayCommand ExportScanReportCommand { get; }
    public RelayCommand ExportExecutionReportCommand { get; }
    public RelayCommand OpenUserFolderCommand { get; }
    public RelayCommand OpenSnapshotFolderCommand { get; }
    /// <summary>打开这台手机自己的数据目录（devices\&lt;序列号&gt;\）。</summary>
    public RelayCommand OpenDeviceFolderCommand { get; }
    /// <summary>把所有手机的档案汇总成一份匿名统计，用来改进名单与程序。</summary>
    public RelayCommand ExportStatisticsCommand { get; }
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
    /// <summary>回滚明细：双击一条快照后列出这次改了哪些东西。</summary>
    public ObservableCollection<RollbackItemViewModel> RollbackItems { get; } = [];
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

    /// <summary>
    /// 强制重新读取权限与安装来源（忽略缓存）。
    /// 默认关：这些字段只随应用更新变化，用缓存能把重复扫描从 10 秒压到 1~2 秒。
    /// </summary>
    public bool ForceDetailRefresh
    {
        get => _settings.ForceDetailRefresh;
        set => SetOption(() => _settings.ForceDetailRefresh, v => _settings.ForceDetailRefresh = v, value,
            () => OnPropertyChanged(nameof(ScanButtonText)));
    }

    /// <summary>扫描按钮的文字跟着「强制重读」变，避免用户点了不知道会走哪条路。</summary>
    public string ScanButtonText => ForceDetailRefresh ? "强制重新读取权限并扫描" : "开始扫描手机";

    /// <summary>
    /// 卸载前把安装包备份到电脑（默认开）。
    /// 这条是「卸载了还能不能装回来」的分界线：商店安装的应用卸载时安装包会被系统删掉。
    /// </summary>
    public bool BackupApksBeforeUninstall
    {
        get => _settings.BackupApksBeforeUninstall;
        set => SetOption(() => _settings.BackupApksBeforeUninstall, v => _settings.BackupApksBeforeUninstall = v, value);
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
            OnPropertyChanged(nameof(AiKeyStatusText));
            RefreshAiAvailability();
        });
    }

    public bool ConsentToSendPackageNames
    {
        get => _settings.ConsentToSendPackageNames;
        set => SetOption(() => _settings.ConsentToSendPackageNames, v => _settings.ConsentToSendPackageNames = v, value,
            RefreshAiAvailability);
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
            if (_snapshot is null) return $"已就绪。先扫描手机，再点下方的「识别未知应用（AI）」。缓存了 {_aiCache.Count} 条历史结果。";
            return $"已就绪。点下方的「识别未知应用（AI）」即可联网识别这次扫描出的未知应用（缓存了 {_aiCache.Count} 条历史结果，命中缓存不重复联网）。";
        }
    }

    /// <summary>「立即识别」按钮是否可用：AI 配好了、而且已经有扫描结果。</summary>
    public bool CanClassifyNow =>
        _snapshot is not null
        && _settings.EnableAi
        && _settings.HasApiKey
        && _settings.ConsentToSendPackageNames;

    public string ScanFilter
    {
        get => _scanFilter;
        set { if (SetProperty(ref _scanFilter, value)) ApplyScanFilter(); }
    }

    /// <summary>类型筛选：全部 / 系统应用 / 第三方应用。</summary>
    public IReadOnlyList<string> TypeFilterOptions { get; } = ["全部", "系统应用", "第三方应用"];

    private string _scanTypeFilter = "全部";
    public string ScanTypeFilter
    {
        get => _scanTypeFilter;
        set { if (SetProperty(ref _scanTypeFilter, value)) ApplyScanFilter(); }
    }

    /// <summary>建议筛选。</summary>
    public IReadOnlyList<string> AdviceFilterOptions { get; } = Advice.FilterOptions;

    private string _scanAdviceFilter = "全部";
    public string ScanAdviceFilter
    {
        get => _scanAdviceFilter;
        set { if (SetProperty(ref _scanAdviceFilter, value)) ApplyScanFilter(); }
    }

    /// <summary>
    /// 权限筛选：可以按具体权限（悬浮窗、安装应用…）筛，也可以按「含高危权限」筛。
    /// 选项来自名单里的 permissionRisks，名单加了新权限这里会自动出现。
    /// </summary>
    public IReadOnlyList<string> PermissionFilterOptions =>
        PermissionAdvice.FilterOptions(_rules.RuleSet.PermissionRisks, _rules.RuleSet.PermissionCombos);

    private string _scanPermissionFilter = "全部";
    public string ScanPermissionFilter
    {
        get => _scanPermissionFilter;
        set { if (SetProperty(ref _scanPermissionFilter, value)) ApplyScanFilter(); }
    }

    private string _planPermissionFilter = "全部";
    public string PlanPermissionFilter
    {
        get => _planPermissionFilter;
        set { if (SetProperty(ref _planPermissionFilter, value)) ApplyPlanFilter(); }
    }

    private string _planAdviceFilter = "全部";
    public string PlanAdviceFilter
    {
        get => _planAdviceFilter;
        set { if (SetProperty(ref _planAdviceFilter, value)) ApplyPlanFilter(); }
    }

    private string _planTypeFilter = "全部";
    public string PlanTypeFilter
    {
        get => _planTypeFilter;
        set { if (SetProperty(ref _planTypeFilter, value)) ApplyPlanFilter(); }
    }

    /// <summary>风险筛选：低 / 中 / 高。</summary>
    public IReadOnlyList<string> RiskFilterOptions { get; } = ["全部", "低", "中", "高"];

    private string _planRiskFilter = "全部";
    public string PlanRiskFilter
    {
        get => _planRiskFilter;
        set { if (SetProperty(ref _planRiskFilter, value)) ApplyPlanFilter(); }
    }

    private string _planSearch = string.Empty;
    public string PlanSearch
    {
        get => _planSearch;
        set { if (SetProperty(ref _planSearch, value)) ApplyPlanFilter(); }
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
            // 演示模式不写深度信息缓存：虚拟设备的包名和版本可能和真机撞上
            _scanner = new DeviceScanner(simulated, new DemoLabelSource(simulated), _catalog,
                log: _log.Info, useDetailCache: false);
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
        _scanner = new DeviceScanner(_adb, catalog: _catalog, log: _log.Info);
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
                UseDeviceArchive(info);
                RefreshSnapshots();
                _log.Success($"演示设备已就绪：{info.DisplayName}（{info.SystemSummary}）");
            }).ConfigureAwait(true);
            return;
        }

        await RunBusyAsync("正在连接手机…", async ct =>
        {
            _log.Step("开始连接设备。");
            var device = await _scanner.EnsureDeviceAsync(ct).ConfigureAwait(true);
            var client = _adb.ForDevice(device.Serial);
            var scanner = new DeviceScanner(client, catalog: _catalog, log: _log.Info);
            var info = await scanner.GetDeviceInfoAsync(ct).ConfigureAwait(true);

            _adb = client;
            _scanner = scanner;
            _device = info;
            IsConnected = true;

            DeviceTitle = info.DisplayName;
            DeviceSubtitle = info.SystemSummary;
            StatusText = "已连接，可以开始扫描";
            UseDeviceArchive(info);
            RefreshSnapshots();
            _log.Success($"已连接：{info.DisplayName}（{info.SystemSummary}），序列号 {info.Serial}");
        }).ConfigureAwait(true);
    }

    public async Task ScanAsync(bool navigateToPlan = true)
    {
        if (!IsConnected) await ConnectAsync().ConfigureAwait(true);
        if (!IsConnected || _scanner is null || _device is null) return;

        await RunBusyAsync("正在扫描手机…", async ct =>
        {
            var progress = new Progress<string>(text => BusyText = text);
            _snapshot = await _scanner
                .ScanPackagesAsync(_device, DeepScan, ForceDetailRefresh, progress, ct)
                .ConfigureAwait(true);

            ScanSummary = $"共 {_snapshot.Packages.Values.Count(p => p.Installed)} 个已安装应用，" +
                          $"其中系统 {_snapshot.SystemCount} 个、第三方 {_snapshot.ThirdPartyCount} 个。" +
                          (_snapshot.DetailEnriched ? "已读取第三方应用的安装来源。" : string.Empty);
            StatusText = $"扫描完成：{ScanSummary}";
            _log.Success(ScanSummary);

            var suggestions = await ClassifyUnknownAsync(ct).ConfigureAwait(true);
            ApplyAiResults(suggestions);
            BuildScanRows();
            BuildPlan(suggestions);

            // 每次扫描都归档：这是「按序列号积累机型/应用/权限数据」的来源。
            // 演示模式是虚拟手机，绝不写进真实档案里，免得把 DEMO0000 混进统计。
            if (!IsDemoMode)
            {
                try
                {
                    var archivePath = DeviceArchive.SaveScan(_snapshot.Device, _snapshot, _rules, _catalog);
                    _log.Info($"本次扫描已归档：{archivePath}");
                }
                catch (Exception ex)
                {
                    _log.Warn($"扫描归档失败（不影响使用）：{ex.Message}");
                }
            }

            if (navigateToPlan) NavigateToTabHandler?.Invoke(1);
        }).ConfigureAwait(true);
    }

    /// <summary>对当前扫描结果立即做一次 AI 识别，不用重新扫描。</summary>
    private async Task ClassifyNowAsync()
    {
        if (_snapshot is null)
        {
            MessageHandler?.Invoke("还没有扫描结果", "请先点「开始扫描手机」，程序才知道这台手机上有哪些应用。");
            return;
        }

        if (!CanClassifyNow)
        {
            MessageHandler?.Invoke("AI 识别还不能用",
                string.IsNullOrWhiteSpace(_settings.ApiKey)
                    ? "请先在上面填入 DeepSeek API Key 并点「保存 Key」。"
                    : "请勾选「启用 AI 识别」和「我同意把包名发送到 DeepSeek」。");
            return;
        }

        await RunBusyAsync("正在识别未知应用…", async ct =>
        {
            var suggestions = await ClassifyUnknownAsync(ct).ConfigureAwait(true);
            ApplyAiResults(suggestions);

            // 识别结果要同时反映到体检表的「建议」列和优化计划里
            BuildScanRows();
            BuildPlan(suggestions);
            NavigateToTabHandler?.Invoke(1);

            var usable = suggestions.Count(s => s.Action != PackageAction.Keep);
            StatusText = usable > 0
                ? $"识别完成：{usable} 个未知应用有结论"
                : "识别完成：这批应用都没有得出可用结论";
            MessageHandler?.Invoke("AI 识别完成",
                usable > 0
                    ? $"对 {suggestions.Count} 个应用做了识别，其中 {usable} 个有可用结论。\n\n" +
                      "它们已经出现在「优化计划」页的「AI 建议」里，**默认不勾选**，" +
                      "请确认后再手动勾上。"
                    : "这次没有得出可用结论（可能模型判为 keep，或网络/额度问题）。\n\n" +
                      "细节可以看「日志」页。");
        }).ConfigureAwait(true);
    }

    private void ApplyAiResults(IReadOnlyList<PackageClassification> suggestions)
    {
        _aiResults = suggestions.ToDictionary(s => s.PackageName, StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(AiStatusText));
        OnPropertyChanged(nameof(CanClassifyNow));
        RefreshCommands();
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
            Catalog = _catalog,
        });

        _allPlanItems.Clear();

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
            vm.ActionChanged += UpdatePlanSummary;
            _allPlanItems.Add(vm);
        }

        ApplyPlanFilter();
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
        OnPropertyChanged(nameof(PlanFilterSummary));
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

        var manualActions = _plan.SelectedItems.Count(i => i.HasActionOverride);
        var manualWarning = manualActions == 0
            ? string.Empty
            : $"\n\n其中 {manualActions} 项的动作是**你手动指定的**（停用 / 卸载），" +
              "会按你选的执行，不再受安全模式影响。";

        var uninstallWarning = _plan.SelectedItems.Any(i => i.EffectiveAction == PackageAction.Uninstall)
            ? BuildUninstallWarning(_plan.SelectedItems.Where(i => i.EffectiveAction == PackageAction.Uninstall))
            : string.Empty;

        var confirmed = ConfirmHandler?.Invoke(
            "确认开始优化",
            $"即将对「{_device?.DisplayName}」执行：\n\n{summary}\n\n" +
            "执行前会自动保存可回滚的快照，随时可以在「回滚」页还原。" +
            manualWarning + uninstallWarning + unknownWarning +
            "\n\n是否继续？") ?? false;
        if (!confirmed) return;

        await RunExecutionAsync(_plan, "正在执行优化…", "优化完成").ConfigureAwait(true);
    }

    /// <summary>
    /// 执行一个计划：先落快照 → 逐项执行 → 回读校验 → 写报告。
    /// 「执行优化」和计划页的「停用 / 卸载」按钮都走这里，保证两条路的行为完全一致。
    /// </summary>
    private async Task RunExecutionAsync(OptimizationPlan plan, string busyText, string doneText)
    {
        if (_adb is null) return;

        await RunBusyAsync(busyText, async ct =>
        {
            var executor = new OptimizationExecutor(_adb, _snapshotStore, _log)
            {
                BackupApksBeforeUninstall = _settings.BackupApksBeforeUninstall,
            };
            var progress = new Progress<ExecutionProgress>(p =>
            {
                BusyText = p.Message;
                if (p.Total > 0) ProgressValue = p.Index * 100.0 / p.Total;
            });

            _lastReport = await executor.ExecuteAsync(plan, progress, ct).ConfigureAwait(true);
            BuildResultRows(_lastReport);
            HasResults = true;
            ResultSummary = $"{_lastReport.Summary}（耗时 {_lastReport.Duration.TotalSeconds:0.0} 秒）";
            StatusText = doneText;

            try
            {
                if (_device is not null && !IsDemoMode)
                {
                    var executionPath = DeviceArchive.SaveExecution(_device, plan, _lastReport);
                    _log.Info($"本次执行已归档：{executionPath}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"执行归档失败（不影响使用）：{ex.Message}");
            }

            AftercareText = BuildAftercare(_lastReport);
            RefreshSnapshots();

            var reportPath = ReportWriter.WriteExecutionReport(
                _lastReport, plan,
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

            var catalogEntry = _catalog.Find(entry.Name);
            _aiResults.TryGetValue(entry.Name, out var aiResult);
            var (adviceText, adviceGroup) = Advice.ForPackage(rule, protection, catalogEntry, entry.HasLauncher, aiResult);

            ScanRows.Add(new PackageRowViewModel
            {
                PackageName = entry.Name,
                // 名字优先级：名单里核对过的中文名 → 从 APK 读出来的真实应用名 → 留空（只显示包名）
                DisplayName = rule?.DisplayName ?? entry.Label ?? string.Empty,
                TypeText = entry.IsThirdParty ? "第三方" : "系统",
                StateText = entry.Disabled ? "停用" : "启用",
                AdviceText = adviceText,
                AdviceGroup = adviceGroup,
                // 系统应用是随系统来的，没有「安装来源」；写「未知」会让人以为查不到
                InstallerText = entry.IsSystem && string.IsNullOrWhiteSpace(entry.Installer)
                    ? "系统预装"
                    : _rules.DescribeInstaller(entry.Installer),
                FirstInstallText = entry.FirstInstallTime is null ? "-" : entry.FirstInstallTime.Value.ToString("yyyy-MM-dd"),
                ListStateText = listState,
                NoteText = BuildDescription(entry, rule, protection),
                IsThirdParty = entry.IsThirdParty,
                // 权限画像只用于展示与筛选，绝不参与自动勾选。
                PermissionProfile = PermissionAdvice.Build(
                    entry.Permissions, _rules.RuleSet.PermissionRisks, _rules.RuleSet.PermissionCombos),
            });
        }

        ApplyScanFilter();
    }

    /// <summary>这个应用是干什么的。名单里有资料就用名单的，没有就给出能帮助判断的线索。</summary>
    private string BuildDescription(PackageEntry entry, RuleRecord? rule, ProtectionMatch protection)
    {
        if (protection.IsProtected && !string.IsNullOrWhiteSpace(protection.Pattern.Reason))
        {
            return $"{protection.Pattern.Category}：{protection.Pattern.Reason}";
        }

        if (!string.IsNullOrWhiteSpace(rule?.Reason)) return rule!.Reason!;

        // 名册里的说明来自 UAD 社区，机翻 + 人工校正
        var catalogEntry = _catalog.Find(entry.Name);
        if (!string.IsNullOrWhiteSpace(catalogEntry?.Display))
        {
            return catalogEntry!.HasChinese
                ? $"{catalogEntry.Display}（UAD 社区资料）"
                : $"{catalogEntry.Display}（UAD 社区原文，未翻译）";
        }

        if (!entry.IsThirdParty)
        {
            return DescribeSystemPackage(entry.Name);
        }

        // 名单外：程序确实不知道它是什么。安装来源已经有单独一列了，
        // 这里只写这一列独有的信息（装入时间 + 系统记录的启动情况），避免整列都是同一句话。
        var parts = new List<string>();
        if (entry.FirstInstallTime is not null) parts.Add($"{entry.FirstInstallTime.Value:yyyy-MM-dd} 装入");
        parts.Add(entry.NeverLaunched switch
        {
            true => "从未启动过",
            false => "启动过",
            _ => "无启动记录",
        });
        return string.Join(" · ", parts);
    }

    /// <summary>系统应用按包名前缀大致归类，比一句「系统组件」有用得多。</summary>
    private static string DescribeSystemPackage(string packageName)
    {
        var name = packageName.ToLowerInvariant();

        if (name.StartsWith("com.qualcomm.") || name.StartsWith("com.mediatek.") || name.StartsWith("com.arm.") || name.StartsWith("com.trustonic."))
            return "芯片厂商的底层组件，属于系统运行的一部分，不要动。";
        if (name.StartsWith("com.android.providers."))
            return "系统数据提供者（联系人、短信、媒体等数据由它提供），不要动。";
        if (name.StartsWith("com.android."))
            return "Android 系统组件，不要动。";
        if (name.StartsWith("com.miui.") || name.StartsWith("com.xiaomi."))
            return "小米系统组件，不要动。";
        if (name.StartsWith("com.coloros.") || name.StartsWith("com.oplus.") || name.StartsWith("com.heytap.") || name.StartsWith("com.oppo."))
            return "OPPO / 一加 系统组件，不要动。";
        if (name.StartsWith("com.vivo.") || name.StartsWith("com.bbk.") || name.StartsWith("com.iqoo."))
            return "vivo / iQOO 系统组件，不要动。";
        if (name.StartsWith("com.huawei.") || name.StartsWith("com.hihonor.") || name.StartsWith("com.honor."))
            return "华为 / 荣耀系统组件，不要动。";
        if (name.StartsWith("com.samsung.") || name.StartsWith("com.sec.android."))
            return "三星系统组件，不要动。";
        if (name.StartsWith("com.google.android."))
            return "Google 系统组件，不要动。";
        if (name.StartsWith("android.") || name == "android")
            return "Android 系统框架组件，不要动。";

        return "系统组件，名单未收录。不确定时不要动。";
    }

    private void ApplyScanFilter()
    {
        var filter = _scanFilter.Trim().ToLowerInvariant();
        var rows = ScanRows.Where(r =>
            MatchesType(r) &&
            Advice.MatchesFilter(r.AdviceGroup, _scanAdviceFilter) &&
            PermissionAdvice.MatchesFilter(r.PermissionProfile, _scanPermissionFilter) &&
            (filter.Length == 0 || r.SearchText.Contains(filter)));

        // 排序即优先级：能被处理的排在前面，用户打开列表就能看到该关注的东西。
        var ordered = rows
            .OrderBy(r => (int)r.AdviceGroup)
            .ThenByDescending(r => r.IsThirdParty)
            .ThenBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ScanRowsView.Clear();
        foreach (var row in ordered) ScanRowsView.Add(row);
        OnPropertyChanged(nameof(ScanRowsView));
        OnPropertyChanged(nameof(ScanFilterSummary));
    }

    private bool MatchesType(PackageRowViewModel row) => _scanTypeFilter switch
    {
        "系统应用" => row.IsSystem,
        "第三方应用" => row.IsThirdParty,
        _ => true,
    };

    public string ScanFilterSummary
    {
        get
        {
            var highRisk = ScanRowsView.Count(r => r.PermissionProfile.HasHighRisk);
            var text = $"显示 {ScanRowsView.Count} / {ScanRows.Count} 个应用";
            return highRisk > 0 ? $"{text}（其中 {highRisk} 个含高危权限）" : text;
        }
    }

    /// <summary>优化计划页的筛选，逻辑和体检页一致。</summary>
    private void ApplyPlanFilter()
    {
        var filter = _planSearch.Trim().ToLowerInvariant();
        var rows = _allPlanItems.Where(i =>
            Advice.MatchesFilter(i.AdviceGroup, _planAdviceFilter) &&
            MatchesPlanType(i) &&
            MatchesPlanRisk(i) &&
            PermissionAdvice.MatchesFilter(i.PermissionProfile, _planPermissionFilter) &&
            (filter.Length == 0 ||
             i.DisplayName.ToLowerInvariant().Contains(filter) ||
             i.Target.ToLowerInvariant().Contains(filter) ||
             i.AdviceText.Contains(filter) ||
             i.PermissionProfile.Hits.Any(h => h.Name.Contains(filter, StringComparison.Ordinal))));

        PlanItems.Clear();
        foreach (var row in rows) PlanItems.Add(row);
        OnPropertyChanged(nameof(PlanItems));
        OnPropertyChanged(nameof(PlanFilterSummary));
    }

    private bool MatchesPlanType(PlanItemViewModel item) => _planTypeFilter switch
    {
        "系统应用" => item.Model.IsSystem,
        "第三方应用" => !item.Model.IsSystem,
        _ => true,
    };

    private bool MatchesPlanRisk(PlanItemViewModel item) => _planRiskFilter switch
    {
        "低" => item.Risk == RiskLevel.Low,
        "中" => item.Risk == RiskLevel.Medium,
        "高" => item.Risk == RiskLevel.High,
        _ => true,
    };

    /// <summary>筛选后要能一眼看到还剩多少条，以及批量勾选会作用在什么范围上。</summary>
    public string PlanFilterSummary
    {
        get
        {
            var filtered = PlanItems.Count != _allPlanItems.Count;
            var text = $"显示 {PlanItems.Count} / {_allPlanItems.Count} 项";
            if (filtered)
            {
                var selected = PlanItems.Count(i => i.IsSelected);
                text += $"（其中已勾选 {selected} 项）—— 上方「全选 / 全不选 / 全选推荐项」只作用于这 {PlanItems.Count} 项";
            }
            return text;
        }
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

    private string _rollbackDetailTitle = "选中一条记录，双击可以看这次改了什么。";
    public string RollbackDetailTitle
    {
        get => _rollbackDetailTitle;
        private set => SetProperty(ref _rollbackDetailTitle, value);
    }

    public bool HasRollbackDetail => RollbackItems.Count > 0;

    /// <summary>
    /// 双击快照后调用：列出这一条快照里改过的每一项，并逐项判断**还能不能恢复**。
    /// 判断要问手机（安装包还在不在），所以先填「检查中…」再逐项更新。
    /// </summary>
    public async Task LoadRollbackDetailAsync()
    {
        RollbackItems.Clear();
        OnPropertyChanged(nameof(HasRollbackDetail));

        var summary = SelectedSnapshot;
        if (summary is null)
        {
            RollbackDetailTitle = "先在上面选中一条记录。";
            return;
        }

        var snapshot = summary.Summary.Snapshot;
        var rows = snapshot.Entries
            .Select(entry => new RollbackItemViewModel
            {
                DisplayName = entry.DisplayName,
                Target = entry.Target,
                ActionText = ActionName(entry.ActionApplied),
                StateText = entry.Kind == PlanItemKind.Setting
                    ? "设置项"
                    : entry.PresenceBefore switch
                    {
                        PackagePresence.Installed => "启用中",
                        PackagePresence.Disabled => "已停用",
                        PackagePresence.RemovedForUser => "已卸载",
                        _ => "未知",
                    },
            })
            .ToList();

        foreach (var row in rows) RollbackItems.Add(row);
        OnPropertyChanged(nameof(HasRollbackDetail));

        var uninstalled = snapshot.Entries.Count(e => e.ActionApplied == PackageAction.Uninstall);
        RollbackDetailTitle = $"{snapshot.CreatedAt:yyyy-MM-dd HH:mm} 那次共 {snapshot.Entries.Count} 项" +
                             (uninstalled > 0 ? $"，其中卸载了 {uninstalled} 个应用（下面会逐个检查能不能装回来）" : "。");

        // 逐项检查能不能恢复：不需要连手机的（停用/后台限制/设置项、有备份的）直接给结论
        foreach (var (row, entry) in rows.Zip(snapshot.Entries))
        {
            if (entry.Kind == PlanItemKind.Setting)
            {
                row.SetResult(Restorability.Restorable, "可写回原来的值");
                continue;
            }

            switch (entry.ActionApplied)
            {
                case PackageAction.Disable:
                    row.SetResult(Restorability.Restorable, "可启用");
                    continue;
                case PackageAction.Restrict:
                    row.SetResult(Restorability.Restorable, "可解除后台限制");
                    continue;
                case PackageAction.Keep:
                    row.SetResult(Restorability.Unknown, "这次没有改动它");
                    continue;
            }

            var backup = ApkBackupStore.Find(snapshot.DeviceSerial, entry.Target);
            if (backup.Count > 0)
            {
                var size = ApkBackupStore.SizeOf(backup) / 1024.0 / 1024;
                row.SetResult(Restorability.Restorable, $"可装回：电脑上有备份（{size:0.0} MB）");
                continue;
            }

            if (entry.IsSystem)
            {
                row.SetResult(Restorability.Restorable, "可装回：系统预装应用");
                continue;
            }

            if (_adb is null)
            {
                row.SetResult(Restorability.Unknown, "连接手机后可以检查能不能装回来");
                continue;
            }

            try
            {
                var (state, message) = await RestorabilityChecker
                    .CheckAsync(_adb, snapshot, entry)
                    .ConfigureAwait(true);
                row.SetResult(state, string.IsNullOrWhiteSpace(message) ? "可尝试恢复" : message);
            }
            catch (Exception ex)
            {
                row.SetResult(Restorability.Unknown, $"检查失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 连上手机之后把「这台手机的数据目录」切过来：
    /// 快照、报告、扫描归档都写到 devices\&lt;序列号&gt;\ 下，多台手机不会互相混。
    /// </summary>
    private void UseDeviceArchive(DeviceInfo info)
    {
        _snapshotStore = new SnapshotStore(IsDemoMode ? _demoDirectory : null, info.Serial);
        OnPropertyChanged(nameof(DeviceDataPath));
        RefreshCommands();
    }

    /// <summary>这台手机的数据目录，显示在界面上，方便直接去翻文件。</summary>
    public string DeviceDataPath => _device is null
        ? "连接手机后，每台手机的数据会按序列号分开存放。"
        : AppPaths.DeviceDirectory(_device.Serial);

    private void OpenDeviceFolder()
    {
        var directory = _device is null ? AppPaths.DevicesDir : AppPaths.DeviceDirectory(_device.Serial);
        Directory.CreateDirectory(directory);
        OpenFolder(directory);
    }

    /// <summary>打开这台手机的快照目录（没连接手机时打开总的 devices 目录）。</summary>
    private void OpenSnapshotFolder()
    {
        var directory = _device is null ? AppPaths.DevicesDir : AppPaths.DeviceSnapshotsDir(_device.Serial);
        Directory.CreateDirectory(directory);
        OpenFolder(directory);
    }

    /// <summary>把所有手机的档案汇总成一份匿名统计，用来改进名单与程序。</summary>
    private void ExportStatistics()
    {
        try
        {
            var text = DeviceArchive.BuildStatistics(AppContext.BaseDirectory);
            var directory = Path.Combine(AppPaths.UserRoot, "stats");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"数据统计-{DateTime.Now:yyyyMMdd-HHmmss}.md");
            File.WriteAllText(path, text, Encoding.UTF8);

            _log.Info($"已导出数据统计：{path}");
            MessageHandler?.Invoke("数据统计已导出",
                $"{path}\n\n" +
                "这份文件汇总了所有手机的机型、系统版本、应用与权限情况，" +
                "**不含序列号、联系人、短信等任何个人信息**，可以直接发给开发者用来改进名单。");
            OpenFolder(directory);
        }
        catch (Exception ex)
        {
            _log.Error($"导出数据统计失败：{ex.Message}");
            MessageHandler?.Invoke("导出失败", ex.Message);
        }
    }

    /// <summary>AI 相关的开关变化后，要重新算一次「立即识别」能不能点。</summary>
    private void RefreshAiAvailability()
    {
        OnPropertyChanged(nameof(AiStatusText));
        OnPropertyChanged(nameof(CanClassifyNow));
        RefreshCommands();
    }

    private void SetAllSelected(bool selected, bool onlySafe)
    {
        // 只作用于当前筛选出来的行：先筛选、再批量勾选是最常用的用法。
        // 没有筛选时 PlanItems 就是全部，行为和以前一致。
        var scope = PlanItems.Where(i => i.IsSelectable).ToList();
        foreach (var item in PlanItems.Where(i => i.IsSelectable))
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

        var selectedNow = scope.Count(i => i.IsSelected);
        var isFiltered = PlanItems.Count != _allPlanItems.Count;
        _log.Step($"{(onlySafe ? "全选推荐项" : selected ? "全选" : "全不选")}：" +
                  $"作用于{(isFiltered ? "筛选结果" : "全部")} {scope.Count} 项，当前共勾选 {selectedNow} 项。");

        OnPropertyChanged(nameof(PlanFilterSummary));
        UpdatePlanSummary();
    }

    /// <summary>
    /// 对**勾选的应用**直接执行「停用」或「卸载」：弹窗确认 → 执行 → 反馈结果。
    ///
    /// 这里刻意不做「先改计划、再点执行优化」那一步——用户已经明确说了要停用还是卸载，
    /// 弹窗让他确认一次就够了。执行的仍然是同一套流程（快照 → 逐项执行 → 回读校验 → 报告）。
    /// </summary>
    private async Task ExecuteSelectedActionAsync(PackageAction action)
    {
        var actionName = ActionName(action);
        var selectedRows = PlanItems.Where(i => i.IsSelected && i.IsSelectable).ToList();

        // 同一个应用可能同时有「后台限制」行和「应用」行，按包名去重，别执行两遍。
        var targets = selectedRows
            .Where(i => i.Model.Kind != PlanItemKind.Setting)
            .GroupBy(i => i.Model.Target, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(i => i.Model.Kind == PlanItemKind.Policy ? 1 : 0).First())
            .ToList();

        if (targets.Count == 0)
        {
            MessageHandler?.Invoke($"没有可{actionName}的应用",
                selectedRows.Count == 0
                    ? "你还没有勾选任何应用。\n\n在列表最左边勾上要处理的应用，再点这个按钮。"
                    : "你勾选的只有「设置项」，它没有停用/卸载的说法。");
            return;
        }

        if (_adb is null || _device is null)
        {
            MessageHandler?.Invoke("还没有连接手机", "先连接手机并扫描一次，再执行操作。");
            return;
        }

        var names = targets.Take(12).Select(i => $"· {i.DisplayName}").ToList();
        if (targets.Count > 12) names.Add($"……还有 {targets.Count - 12} 个");

        var effect = action == PackageAction.Uninstall
            ? "卸载是「为用户卸载」：应用数据保留，随时能在「回滚」页装回来。"
            : "停用后应用不能再运行、图标会消失，随时能在「回滚」页启用。";

        if (action == PackageAction.Uninstall) effect += BuildUninstallWarning(targets.Select(t => t.Model));

        var confirmed = ConfirmHandler?.Invoke(
            $"确认{actionName}这 {targets.Count} 个应用",
            $"即将对「{_device.DisplayName}」{actionName}：\n\n{string.Join("\n", names)}\n\n" +
            effect + "\n执行前会自动保存快照。\n\n是否继续？") ?? false;
        if (!confirmed) return;

        // 执行器按 EffectiveAction 办事，所以这里临时挂上用户选的动作；
        // 执行完立刻摘掉——「计划里的动作」和「这一次的执行」是两件事，别混在一起。
        var touched = new List<PlanItemViewModel>();
        _lastReport = null;
        try
        {
            var items = new List<PlanItem>();
            foreach (var row in targets)
            {
                row.Model.ActionOverride = action;
                row.Model.IsSelected = true;
                touched.Add(row);
                items.Add(row.Model);
            }

            var plan = new OptimizationPlan
            {
                Device = _device,
                Tier = _settings.LastTier,
                Items = items,
                Skipped = [],
                ListVersion = _rules.ListVersion,
                Warnings = [],
            };

            await RunExecutionAsync(plan, $"正在{actionName}…", $"已{actionName} {targets.Count} 个应用")
                .ConfigureAwait(true);
        }
        finally
        {
            foreach (var row in touched)
            {
                row.Model.ActionOverride = null;
                row.RaiseActionChanged();
            }
        }

        if (_lastReport is not null)
        {
            MessageHandler?.Invoke($"{actionName}结果", BuildResultMessage(_lastReport));
        }

        // 手机上已经变了，列表也得跟着变；走缓存所以很快。
            await ScanAsync(navigateToPlan: false).ConfigureAwait(true);
    }

    /// <summary>
    /// 卸载前把「能不能装回来」讲清楚：商店安装的应用卸载时安装包会被系统一起删掉，
    /// 只有电脑上有备份才装得回来；系统预装应用则随时能从系统副本恢复。
    /// </summary>
    private string BuildUninstallWarning(IEnumerable<PlanItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return string.Empty;

        var serial = _device?.Serial ?? string.Empty;
        var thirdParty = list.Where(i => !i.IsSystem).ToList();
        var withoutBackup = thirdParty
            .Where(i => ApkBackupStore.Find(serial, i.Target).Count == 0)
            .ToList();
        var system = list.Count - thirdParty.Count;

        var text = new StringBuilder();
        if (system > 0)
        {
            text.AppendLine();
            text.Append($"其中 {system} 个是系统预装应用：系统分区上还有内置副本，随时能装回来。");
        }

        if (thirdParty.Count == 0) return text.ToString();

        text.AppendLine();
        if (_settings.BackupApksBeforeUninstall)
        {
            if (withoutBackup.Count > 0)
            {
                text.Append($"其余 {thirdParty.Count} 个是商店安装的应用：卸载时**安装包会被系统删掉**，" +
                            $"程序会先把安装包备份到电脑（{withoutBackup.Count} 个还没备份过，视大小需要等一会儿），" +
                            "之后才能在「回滚」页装回来。");
            }
            else
            {
                text.Append($"其余 {thirdParty.Count} 个是商店安装的应用：电脑上已经有备份，可以随时装回来。");
            }
        }
        else if (withoutBackup.Count > 0)
        {
            text.Append($"⚠ 其中 {withoutBackup.Count} 个是商店安装的应用，而「卸载前备份安装包」是关着的——" +
                        "**这几个卸载之后就装不回来了，只能重新下载**。建议先关掉这个窗口，去左侧把备份勾上。");
        }

        return text.ToString();
    }

    /// <summary>执行完给一句人话总结：成功多少、降级多少、失败的是哪几个。</summary>
    private static string BuildResultMessage(ExecutionReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(report.Summary);

        var downgraded = report.Results.Count(r => r.Status == ActionStatus.Downgraded);
        if (downgraded > 0)
        {
            text.AppendLine();
            text.AppendLine($"其中 {downgraded} 项系统不允许原动作，已自动降级为更保守的做法（效果相同，仍可一键还原）。");
        }

        var failed = report.Results.Where(r => r.Status == ActionStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            text.AppendLine();
            text.AppendLine($"失败 {failed.Count} 项：");
            foreach (var item in failed.Take(8))
            {
                text.AppendLine($"· {item.Item.DisplayName}：{item.Message}");
            }
        }

        text.AppendLine();
        text.AppendLine("详细结果见「执行结果」页；要还原就去「回滚」页。");
        return text.ToString();
    }

    private static string ActionName(PackageAction action) => action switch
    {
        PackageAction.Disable => "停用",
        PackageAction.Uninstall => "卸载",
        PackageAction.Restrict => "后台限制",
        PackageAction.Settings => "修改设置",
        PackageAction.Keep => "未改动",
        _ => action.ToString(),
    };

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
            RefreshAiAvailability();
        }
        MessageHandler?.Invoke("AI 设置", "设置已保存。\n\nAPI Key 使用 Windows 凭据加密后保存在本机，不会写入程序目录，也不会上传到任何服务器。");
    }

    private void ClearAiKey()
    {
        _settings.ApiKey = null;
        _settings.Save();
        OnPropertyChanged(nameof(AiKeyStatusText));
        RefreshAiAvailability();
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
        ClassifyNowCommand.RaiseCanExecuteChanged();
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
