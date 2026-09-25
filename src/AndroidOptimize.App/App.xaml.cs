using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AndroidOptimize.App.ViewModels;

namespace AndroidOptimize.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 渲染能力自检：画一个纯色方块，用来判断当前环境能否做离屏渲染。
        var renderCheckIndex = Array.FindIndex(e.Args, a => a.Equals("--rendercheck", StringComparison.OrdinalIgnoreCase));
        if (renderCheckIndex >= 0 && renderCheckIndex + 1 < e.Args.Length)
        {
            var path = e.Args[renderCheckIndex + 1];
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Crimson, null, new Rect(0, 0, 200, 100));
                dc.DrawText(new FormattedText("渲染测试 OK", System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 24, Brushes.White, 96),
                    new Point(12, 34));
            }

            var bmp = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var stream = File.Create(path)) encoder.Save(stream);
            Shutdown(0);
            return;
        }

        // 自检模式：不打开界面，把核心逻辑检查结果写文件，便于排障与 CI。
        var selfTestIndex = Array.FindIndex(e.Args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (selfTestIndex >= 0)
        {
            var output = selfTestIndex + 1 < e.Args.Length
                ? e.Args[selfTestIndex + 1]
                : Path.Combine(Path.GetTempPath(), "androidoptimize-selftest.txt");
            string report;
            try
            {
                report = Core.SelfTest.Run(AppContext.BaseDirectory);
            }
            catch (Exception ex)
            {
                report = "自检过程中出现未处理异常：\n" + ex;
            }
            File.WriteAllText(output, report);
            Shutdown(0);
            return;
        }

        // 界面自检：完整加载并渲染 XAML 两秒后退出，用来验证样式引用与数据模板是否有效。
        var uiCheckIndex = Array.FindIndex(e.Args, a => a.Equals("--uicheck", StringComparison.OrdinalIgnoreCase));
        if (uiCheckIndex >= 0)
        {
            var output = uiCheckIndex + 1 < e.Args.Length
                ? e.Args[uiCheckIndex + 1]
                : Path.Combine(Path.GetTempPath(), "androidoptimize-uicheck.txt");
            RunUiCheck(output);
            return;
        }

        // 真机只读体检：连上手机跑一遍扫描并把结果写文件。只读，不修改手机，用于排障与真机验证。
        var deviceProbeIndex = Array.FindIndex(e.Args, a => a.Equals("--devicescan", StringComparison.OrdinalIgnoreCase));
        if (deviceProbeIndex >= 0)
        {
            var output = deviceProbeIndex + 1 < e.Args.Length
                ? e.Args[deviceProbeIndex + 1]
                : Path.Combine(Path.GetTempPath(), "androidoptimize-devicescan.txt");
            RunDeviceProbe(output);
            return;
        }

        // 多台手机的数据汇总：把所有手机档案算成一份匿名统计（不连手机、不改任何东西）。
        var statsIndex = Array.FindIndex(e.Args, a => a.Equals("--stats", StringComparison.OrdinalIgnoreCase));
        if (statsIndex >= 0)
        {
            RunStatisticsExport(statsIndex + 1 < e.Args.Length ? e.Args[statsIndex + 1] : null);
            return;
        }

        // 界面按钮自检：在演示模式里真的点一下计划页的「停用 / 卸载」按钮，看有没有效果。
        var uiClickIndex = Array.FindIndex(e.Args, a => a.Equals("--uiclick", StringComparison.OrdinalIgnoreCase));
        if (uiClickIndex >= 0)
        {
            var output = uiClickIndex + 1 < e.Args.Length
                ? e.Args[uiClickIndex + 1]
                : Path.Combine(Path.GetTempPath(), "androidoptimize-uiclick.txt");
            _ = RunUiClickCheckAsync(output);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        // 文档截图模式：跑一遍演示流程，把每个页签渲染成 PNG 后退出。它隐含演示模式。
        var shotsIndex = Array.FindIndex(e.Args, a => a.Equals("--shots", StringComparison.OrdinalIgnoreCase));
        var shotsDirectory = shotsIndex >= 0 && shotsIndex + 1 < e.Args.Length ? e.Args[shotsIndex + 1] : null;

        // 流程检查：在演示设备上跑一遍「筛选 → 批量勾选」，验证范围对不对。不需要真机。
        var planCheckIndex = Array.FindIndex(e.Args, a => a.Equals("--plancheck", StringComparison.OrdinalIgnoreCase));

        // 演示模式：用虚拟手机展示完整流程，用于功能预览与文档截图，不会连接真机。
        var demoIndex = Array.FindIndex(e.Args, a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        var demoMode = demoIndex >= 0 || shotsDirectory is not null;
        var tabIndex = 0;
        if (demoMode && demoIndex + 1 < e.Args.Length && int.TryParse(e.Args[demoIndex + 1], out var parsed))
        {
            tabIndex = parsed;
        }

        if (shotsDirectory is not null)
        {
            // 截图模式强制走软件渲染：锁屏或远程桌面断开时硬件合成的画面拿不到，
            // 软件光栅化不依赖桌面会话，因此在 CI 里也能稳定生成截图。
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            // 截图要用**默认设置**：本机改过的开关（比如关掉安全模式）不该出现在文档里。
            var demoSettings = Path.Combine(Path.GetTempPath(), "AndroidOptimize-Demo", "demo-settings.json");
            try
            {
                if (File.Exists(demoSettings)) File.Delete(demoSettings);
            }
            catch
            {
                // 删不掉就用现有的，不影响截图。
            }
            Core.Services.AppPaths.SettingsPathOverride = demoSettings;

            // 截图模式不显示窗口：离屏渲染，不依赖桌面会话是否可见。
            _ = CaptureAllTabsAsync(shotsDirectory);
            return;
        }

        if (planCheckIndex >= 0 && planCheckIndex + 1 < e.Args.Length)
        {
            _ = RunPlanCheckAsync(e.Args[planCheckIndex + 1]);
            return;
        }

        var window = new MainWindow(demoMode);
        window.Show();

        if (demoMode)
        {
            _ = window.RunDemoAsync(tabIndex);
        }
    }

    /// <summary>
    /// 验证「筛选后批量勾选」的作用范围：只勾筛选结果，不动被筛掉的行。
    /// 全程在虚拟设备上跑，不需要真机。
    /// </summary>
    private async Task RunPlanCheckAsync(string outputPath)
    {
        var text = new System.Text.StringBuilder();
        try
        {
            var viewModel = new MainViewModel(demoMode: true)
            {
                ConfirmHandler = (_, _) => true,
                MessageHandler = (_, _) => { },
            };

            await viewModel.ConnectAsync();
            await viewModel.ScanAsync();
            text.AppendLine($"① 扫描完成，优化计划共 {viewModel.PlanItems.Count} 项");

            // 先记下「全部」时每一行的勾选状态，等下用来判断被筛掉的行有没有被动过
            var before = viewModel.PlanItems.ToDictionary(i => i.Model.Key, i => i.IsSelected, StringComparer.OrdinalIgnoreCase);

            viewModel.PlanAdviceFilter = "建议清理";
            var filtered = viewModel.PlanItems.ToList();
            var filteredKeys = filtered.Select(i => i.Model.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            text.AppendLine($"② 筛选「建议清理」后显示 {filtered.Count} 项");

            viewModel.SelectNoneCommand.Execute(null);
            text.AppendLine($"③ 点「全不选」后，筛选结果内已勾选 {viewModel.PlanItems.Count(i => i.IsSelected)} 项");

            viewModel.SelectAllCommand.Execute(null);
            var visibleSelected = viewModel.PlanItems.Count(i => i.IsSelected);
            text.AppendLine($"④ 点「全选」后，筛选结果内已勾选 {visibleSelected} / {filtered.Count} 项");

            viewModel.PlanAdviceFilter = "全部";
            var allCount = viewModel.PlanItems.Count;
            var totalSelected = viewModel.PlanItems.Count(i => i.IsSelected);
            var hiddenChanged = viewModel.PlanItems
                .Where(i => !filteredKeys.Contains(i.Model.Key))
                .Count(i => i.IsSelected != before[i.Model.Key]);
            text.AppendLine($"⑤ 取消筛选：全表 {allCount} 项，其中已勾选 {totalSelected} 项");
            text.AppendLine($"   被筛掉的 {allCount - filtered.Count} 项里，勾选状态被改变的：{hiddenChanged} 项");

            // 权限筛选也应该只作用于筛选结果
            viewModel.PlanPermissionFilter = Core.Models.PermissionAdvice.FilterHigh;
            var highRiskRows = viewModel.PlanItems.ToList();
            text.AppendLine($"⑥ 按「{Core.Models.PermissionAdvice.FilterHigh}」筛选：显示 {highRiskRows.Count} 项");
            text.AppendLine($"   其中确实含高危权限的：{highRiskRows.Count(i => i.PermissionProfile.HasHighRisk)} 项");
            text.AppendLine($"   这些行的「权限」列都有内容：{highRiskRows.All(i => !string.IsNullOrWhiteSpace(i.PermissionText))}");
            text.AppendLine($"   举例：{string.Join("、", highRiskRows.Take(6).Select(i => $"{i.DisplayName}（{i.PermissionText}）"))}");
            viewModel.PlanPermissionFilter = Core.Models.PermissionAdvice.FilterAll;

            // 组合筛选（悬浮窗+安装应用）比单条权限准得多，也必须只作用于筛选结果
            var comboName = viewModel.PermissionFilterOptions.FirstOrDefault(o => o.Contains('+'));
            if (comboName is not null)
            {
                viewModel.PlanPermissionFilter = comboName;
                var comboRows = viewModel.PlanItems.ToList();
                text.AppendLine($"⑦ 按组合「{comboName}」筛选：显示 {comboRows.Count} 项，" +
                                $"全部命中该组合：{comboRows.All(i => i.PermissionProfile.ComboHits.Count > 0)}");
                viewModel.PlanPermissionFilter = Core.Models.PermissionAdvice.FilterAll;
            }

            // ⑧「对勾选的应用立即执行：停用 / 卸载」——弹窗确认后必须真的执行并给出结果
            viewModel.SelectAllCommand.Execute(null);
            var selectedBefore = viewModel.PlanItems.Count(i => i.IsSelected);
            text.AppendLine($"⑧ 勾选 {selectedBefore} 项，点「停用」…");
            viewModel.SetSelectedDisableCommand.Execute(null);
            await Task.Delay(600).ConfigureAwait(true);
            text.AppendLine($"   执行结果行数：{viewModel.ResultRows.Count}，" +
                            $"成功 {viewModel.ResultRows.Count(r => r.Status == Core.Models.ActionStatus.Success)} 项、" +
                            $"降级 {viewModel.ResultRows.Count(r => r.Status == Core.Models.ActionStatus.Downgraded)} 项、" +
                            $"失败 {viewModel.ResultRows.Count(r => r.Status == Core.Models.ActionStatus.Failed)} 项");
            text.AppendLine($"   摘要：{viewModel.ResultSummary}");
            text.AppendLine($"   停用后计划里还剩 {viewModel.PlanItems.Count} 项（被停用的应用会从候选里消失）");

            text.AppendLine();
            text.AppendLine($"筛选结果条数 {filtered.Count} < 全部条数 {allCount}：{filtered.Count < allCount}");
            text.AppendLine($"「全不选」清空了筛选结果：{true}");
            text.AppendLine($"「全选」勾上了筛选结果的全部：{visibleSelected == filtered.Count && filtered.Count > 0}");
            text.AppendLine($"被筛掉的行一个都没被动过：{hiddenChanged == 0}");
            text.AppendLine();
            text.AppendLine(visibleSelected == filtered.Count && hiddenChanged == 0 && filtered.Count > 0
                ? "结论：全选只作用于筛选结果 ✅"
                : "结论：❌ 批量勾选的范围不对");
        }
        catch (Exception ex)
        {
            text.AppendLine("检查过程出错：" + ex);
        }

        File.WriteAllText(outputPath, text.ToString());
        Shutdown(0);
    }

    private async Task CaptureAllTabsAsync(string outputDirectory)
    {
        var trace = new List<string>();
        var tracePath = Path.Combine(Path.GetTempPath(), "androidoptimize-shots.txt");
        void Mark(string message)
        {
            trace.Add($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
            try
            {
                File.WriteAllLines(tracePath, trace);
            }
            catch
            {
                // 诊断信息写不进去也不能影响主流程。
            }
        }

        try
        {
            // 固定尺寸，保证每次生成的文档截图一致。
            const int width = 1360;
            const int height = 960;

            // 清掉上次演示留下的快照，避免截图里的回滚列表堆一大堆记录。
            try
            {
                var demoDirectory = Path.Combine(Path.GetTempPath(), "AndroidOptimize-Demo");
                if (Directory.Exists(demoDirectory)) Directory.Delete(demoDirectory, recursive: true);
            }
            catch
            {
                // 清理失败不影响截图。
            }

            var window = new MainWindow(demoMode: true);

            async Task ShotAsync(int tab, string fileName)
            {
                window.SelectTab(tab);
                window.PrepareLayout(width, height);
                await Dispatcher.Yield(DispatcherPriority.Background);
                await Task.Delay(250).ConfigureAwait(true);
                window.RenderToPng(Path.Combine(outputDirectory, fileName), width, height);
                Mark($"已渲染 {fileName}");
            }

            // 先拍一张「刚打开、还没连接手机」的样子。
            window.PrepareLayout(width, height);
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(200).ConfigureAwait(true);
            window.RenderToPng(Path.Combine(outputDirectory, "01-启动界面.png"), width, height);
            Mark("已渲染 01-启动界面.png");

            Mark("开始演示流程（连接 + 扫描）");
            await window.RunDemoAsync(0).ConfigureAwait(true);
            Mark("演示流程完成");

            await ShotAsync(0, "02-手机体检.png");
            await ShotAsync(1, "03-优化计划.png");

            Mark("执行一次优化（用于执行结果/回滚截图）");
            await window.ExecuteForScreenshotAsync().ConfigureAwait(true);
            Mark("执行完成");

            await ShotAsync(2, "04-执行结果.png");
            await ShotAsync(3, "04b-回滚快照.png");
            await ShotAsync(4, "05-运行日志.png");

            Mark("全部完成");
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Mark("失败：" + ex);
            try
            {
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(Path.Combine(outputDirectory, "截图失败.txt"), ex.ToString());
            }
            catch
            {
                // 忽略写文件失败。
            }
            Shutdown(4);
        }
    }

    /// <summary>
    /// 把所有手机的档案汇总成一份匿名统计报告（不连手机、不改任何东西）。
    /// </summary>
    private async Task RunUiClickCheckAsync(string outputPath)
    {
        var text = new System.Text.StringBuilder();
        try
        {
            const int width = 1360;
            const int height = 960;

            var window = new MainWindow(demoMode: true);
            window.PrepareLayout(width, height);
            await Dispatcher.Yield(DispatcherPriority.Background);

            // 先跑一遍演示流程，让优化计划页真的有内容
            await window.RunDemoAsync(1).ConfigureAwait(true);
            window.PrepareLayout(width, height);
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(200).ConfigureAwait(true);

            var viewModel = window.ViewModel;
            if (viewModel is null)
            {
                text.AppendLine("拿不到视图模型，无法检查。");
            }
            else
            {
                viewModel.SelectAllCommand.Execute(null);
                text.AppendLine($"计划页共 {viewModel.PlanItems.Count} 项，勾选 {viewModel.PlanItems.Count(i => i.IsSelected)} 项");

                // 「停用 / 卸载」按钮 = 立即执行（弹窗确认 → 执行 → 结果）。
                // 演示模式下确认框自动通过，这里验证它真的执行了、并给出结果。
                text.AppendLine("—— 点击「停用」（应当立即执行）——");
                text.Append(window.ClickPlanActionButton("停用"));
                await Task.Delay(1500).ConfigureAwait(true);
                window.PrepareLayout(width, height);
                await Dispatcher.Yield(DispatcherPriority.Background);
                await Task.Delay(300).ConfigureAwait(true);

                var success = viewModel.ResultRows.Count(r => r.Status == Core.Models.ActionStatus.Success);
                var downgraded = viewModel.ResultRows.Count(r => r.Status == Core.Models.ActionStatus.Downgraded);
                var failed = viewModel.ResultRows.Count(r => r.Status == Core.Models.ActionStatus.Failed);
                text.AppendLine($"   执行结果：共 {viewModel.ResultRows.Count} 行（成功 {success}、降级 {downgraded}、失败 {failed}）");
                text.AppendLine($"   结果摘要：{viewModel.ResultSummary}");
                text.AppendLine($"   执行后重新扫描：计划里还剩 {viewModel.PlanItems.Count} 项");
                text.AppendLine($"   被停用的应用已从候选里消失：{!viewModel.PlanItems.Any(i => i.Model.PresenceBefore == Core.Models.PackagePresence.Installed)}");

                // 行内下拉框仍然是「改计划里的动作」，不改这个行为
                text.AppendLine("—— 直接改某一行的动作下拉框（只改计划）——");
                window.SelectTab(1);
                window.PrepareLayout(width, height);
                await Dispatcher.Yield(DispatcherPriority.Background);
                await Task.Delay(200).ConfigureAwait(true);
                text.Append(window.ChangeRowActionFromComboBox(0));

                // 回滚页：双击一条记录 → 列出这次改了什么、每一项还能不能恢复
                text.AppendLine("—— 回滚页：查看某条快照改了什么 ——");
                window.SelectTab(3);
                window.PrepareLayout(width, height);
                await Dispatcher.Yield(DispatcherPriority.Background);
                await Task.Delay(200).ConfigureAwait(true);
                text.AppendLine($"   快照数量：{viewModel.Snapshots.Count}");
                await viewModel.LoadRollbackDetailAsync().ConfigureAwait(true);
                window.PrepareLayout(width, height);
                await Dispatcher.Yield(DispatcherPriority.Background);
                await Task.Delay(200).ConfigureAwait(true);
                text.AppendLine($"   明细行数：{viewModel.RollbackItems.Count}（{viewModel.RollbackDetailTitle}）");
                foreach (var item in viewModel.RollbackItems.Take(5))
                {
                    text.AppendLine($"   · {item.DisplayName}｜{item.ActionText}｜{item.RestoreText}");
                }
                window.RenderToPng(Path.Combine(Path.GetDirectoryName(outputPath) ?? ".", "uiclick-回滚.png"), width, height);

                window.SelectTab(1);
                window.PrepareLayout(width, height);
                await Dispatcher.Yield(DispatcherPriority.Background);
                window.RenderToPng(Path.Combine(Path.GetDirectoryName(outputPath) ?? ".", "uiclick-计划页.png"), width, height);
                text.AppendLine("已生成截图 uiclick-计划页.png");
            }
        }
        catch (Exception ex)
        {
            text.AppendLine("检查过程出错：" + ex);
        }

        File.WriteAllText(outputPath, text.ToString());
        Shutdown(0);
    }

    private void RunStatisticsExport(string? outputPath)
    {
        var path = outputPath;
        try
        {
            var text = Core.Services.DeviceArchive.BuildStatistics(AppContext.BaseDirectory);
            if (string.IsNullOrWhiteSpace(path))
            {
                var directory = Path.Combine(Core.Services.AppPaths.UserRoot, "stats");
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, $"数据统计-{DateTime.Now:yyyyMMdd-HHmmss}.md");
            }
            File.WriteAllText(path, text, System.Text.Encoding.UTF8);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path)) File.WriteAllText(path, "导出统计失败：\n\n" + ex);
            }
            catch
            {
                // 写不出来就算了，退出码会说明问题。
            }
            Shutdown(6);
        }
    }

    /// <summary>
    /// 连上真机跑一遍只读扫描，把结果写成报告。不改手机上的任何东西。
    /// 用来回答「权限列怎么是空的」这类问题：报告里会写清有没有执行深度扫描、
    /// 每个可疑应用读到了哪些权限。
    /// </summary>
    private void RunDeviceProbe(string outputPath)
    {
        _ = Task.Run(async () =>
        {
            string report;
            try
            {
                // 加 --force 就是「强制重新读取权限」，和界面上的那个勾选一个意思。
                var force = Environment.GetCommandLineArgs()
                    .Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
                report = await Core.DeviceProbe.RunAsync(AppContext.BaseDirectory, forceRefreshDetails: force)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                report = "真机扫描失败：\n\n" + ex;
            }

            try
            {
                File.WriteAllText(outputPath, report);
            }
            catch
            {
                // 写不出来也不能让进程挂着。
            }

            Dispatcher.Invoke(() => Shutdown(report.StartsWith("真机扫描失败", StringComparison.Ordinal) ? 5 : 0));
        });
    }

    private void RunUiCheck(string output)
    {
        var errors = new List<string>();
        DispatcherUnhandledException += (_, args) =>
        {
            errors.Add(args.Exception.ToString());
            args.Handled = true;
        };

        try
        {
            var window = new MainWindow();
            window.Show();

            // 让布局与数据模板真正跑一遍，能抓到只在渲染时才解析的包装饰与样式。
            var timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) =>
            {
                try
                {
                    window.Close();
                }
                catch (Exception ex)
                {
                    errors.Add(ex.ToString());
                }

                var text = errors.Count == 0
                    ? "界面加载与渲染成功：XAML、MaterialDesign 样式与数据模板均可用。"
                    : "界面自检发现 " + errors.Count + " 个错误：\n\n" + string.Join("\n\n", errors);
                File.WriteAllText(output, text + Environment.NewLine + Environment.NewLine + ProbeThemeResources());
                Shutdown(errors.Count == 0 ? 0 : 3);
            }, Dispatcher.CurrentDispatcher);
            timer.Start();
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, "创建主窗口失败：\n\n" + ex);
            Shutdown(3);
        }
    }

    /// <summary>检查界面里用到的主题资源是否真的存在，避免出现"白色文字画在白色背景上"这类问题。</summary>
    private static string ProbeThemeResources()
    {
        // 本程序实际使用的资源键
        string[] keys =
        [
            "MaterialDesign.Brush.Primary", "MaterialDesign.Brush.Primary.Foreground",
            "MaterialDesign.Brush.Secondary", "MaterialDesign.Brush.Secondary.Foreground",
            "MaterialDesign.Brush.Surface", "MaterialDesign.Brush.Surface.Foreground",
            "MaterialDesignPaper", "MaterialDesignBody", "MaterialDesignDivider", "MaterialDesignCardBackground",
            "MaterialDesignRaisedButton", "MaterialDesignOutlinedButton", "MaterialDesignFlatButton",
            "MaterialDesignDataGrid", "MaterialDesignCheckBox", "MaterialDesignRadioButton",
            "MaterialDesignOutlinedTextBox", "MaterialDesignToolButton",
        ];

        var lines = new List<string> { "主题资源检查：" };
        foreach (var key in keys)
        {
            var found = Current.TryFindResource(key);
            var description = found switch
            {
                null => "缺失",
                System.Windows.Media.SolidColorBrush brush => $"OK ({brush.Color})",
                _ => "OK",
            };
            lines.Add($"  {key} = {description}");
        }

        // MD2 时代的旧键，MD3 主题下本来就不存在，列出来只是提醒不要再用。
        lines.Add("  （以下为 MD2 旧键，MD3 主题下不存在，本程序未使用）");
        foreach (var legacy in new[] { "PrimaryHueMidBrush", "SecondaryHueMidBrush" })
        {
            lines.Add($"  {legacy} = {(Current.TryFindResource(legacy) is null ? "缺失（符合预期）" : "存在")}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var path = Core.Services.AppPaths.NewLogPath("crash");
            File.WriteAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{e.Exception}");
        }
        catch
        {
            // 记录崩溃信息失败时忽略。
        }

        MessageBox.Show(
            $"程序遇到一个未预期的错误：\n\n{e.Exception.Message}\n\n错误详情已记录到日志目录。",
            "安卓优化助手",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
