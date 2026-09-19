using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AndroidOptimize.App.ViewModels;

namespace AndroidOptimize.App;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow(bool demoMode = false)
    {
        InitializeComponent();

        _viewModel = new MainViewModel(demoMode)
        {
            ConfirmHandler = (title, message) => demoMode || MessageBox.Show(
                this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK,
            MessageHandler = (title, message) =>
            {
                // 演示模式不弹窗，避免截图时被对话框挡住。
                if (!demoMode)
                {
                    MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
                }
            },
            NavigateToTabHandler = index =>
            {
                if (index >= 0 && index < MainTabs.Items.Count) MainTabs.SelectedIndex = index;
            },
        };

        DataContext = _viewModel;
        Closed += (_, _) => _viewModel?.Dispose();

        if (demoMode)
        {
            Title = "安卓优化助手 · 演示模式";
        }
    }

    /// <summary>演示模式专用：自动连接虚拟设备、扫描、必要时执行，然后切到指定页签。</summary>
    public async Task RunDemoAsync(int tabIndex)
    {
        if (_viewModel is null) return;

        await _viewModel.ConnectAsync().ConfigureAwait(true);
        await _viewModel.ScanAsync().ConfigureAwait(true);

        if (tabIndex >= 2)
        {
            await _viewModel.ExecuteAsync().ConfigureAwait(true);
        }

        SelectTab(tabIndex);
    }

    public void SelectTab(int tabIndex)
    {
        if (tabIndex >= 0 && tabIndex < MainTabs.Items.Count)
        {
            MainTabs.SelectedIndex = tabIndex;
        }
    }

    /// <summary>截图流程用：单独触发一次执行，避免重复扫描。</summary>
    public Task ExecuteForScreenshotAsync() => _viewModel?.ExecuteAsync() ?? Task.CompletedTask;

    /// <summary>截图流程用：应用懒人模式，展示勾选效果。</summary>
    public void ApplyLazyModeForScreenshot() => _viewModel?.ApplyLazyModeCommand.Execute(null);

    /// <summary>
    /// 把当前界面渲染成 PNG。用 WPF 自己的渲染管线，不依赖桌面会话是否可见，
    /// 因此在锁屏、远程桌面断开、CI 环境里也能生成一致的文档截图。
    /// </summary>
    /// <summary>
    /// 把界面离屏渲染成 PNG。
    /// 关键在于：不渲染 Window（顶层的渲染依赖桌面会话，锁屏或远程桌面断开时会得到空白图），
    /// 而是把内容根元素手动 Measure/Arrange 之后直接渲染，这属于纯软件渲染，任何情况下都可用。
    /// </summary>
    public void RenderToPng(string path, int width, int height)
    {
        PrepareLayout(width, height);

        if (Content is not FrameworkElement root)
        {
            throw new InvalidOperationException("窗口内容尚未初始化，无法截图。");
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// 先做一次布局预热。
    /// 窗口内容从没真正显示过时，第一次 Measure/Arrange 只是把外层模板（TabControl 等）展开，
    /// 内层控件要到下一轮才真正生成行与单元格；不预热就会渲染出空表格。
    /// </summary>
    public void PrepareLayout(int width, int height)
    {
        if (Content is not FrameworkElement root) return;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is PasswordBox box)
        {
            _viewModel.ApiKeyInput = box.Password;
        }
    }
}
