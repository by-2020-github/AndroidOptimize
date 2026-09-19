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

        DispatcherUnhandledException += OnUnhandledException;

        // 文档截图模式：跑一遍演示流程，把每个页签渲染成 PNG 后退出。它隐含演示模式。
        var shotsIndex = Array.FindIndex(e.Args, a => a.Equals("--shots", StringComparison.OrdinalIgnoreCase));
        var shotsDirectory = shotsIndex >= 0 && shotsIndex + 1 < e.Args.Length ? e.Args[shotsIndex + 1] : null;

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

            // 截图模式不显示窗口：离屏渲染，不依赖桌面会话是否可见。
            _ = CaptureAllTabsAsync(shotsDirectory);
            return;
        }

        var window = new MainWindow(demoMode);
        window.Show();

        if (demoMode)
        {
            _ = window.RunDemoAsync(tabIndex);
        }
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

            // 再拍一张「点了懒人模式之后」的样子，用来说明这个功能的效果。
            Mark("应用懒人模式");
            window.ApplyLazyModeForScreenshot();
            await ShotAsync(1, "06-懒人模式.png");

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
