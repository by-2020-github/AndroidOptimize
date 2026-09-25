using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AndroidOptimize.App.ViewModels;
using AndroidOptimize.Core.Models;

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

    /// <summary>界面自检用：把当前视图模型露出来，好让 <c>--uiclick</c> 检查按钮点下去有没有效。</summary>
    internal MainViewModel? ViewModel => _viewModel;

    /// <summary>
    /// 像用户那样点一下计划页的「停用 / 卸载」按钮（走 WPF 自动化，等价于真实的点击），
    /// 返回按钮状态说明。用来验证「按钮点了没反应」这类问题到底卡在哪一环。
    /// </summary>
    public string ClickPlanActionButton(string label)
    {
        var text = new StringBuilder();
        // 注意：窗口没有 Show() 过时，VisualTreeHelper 从 Window 往下是空的，
        // 必须从 Content（根元素）开始遍历——截图流程也是这么渲染的。
        var root = Content as DependencyObject ?? this;
        var buttons = FindVisualChildren<Button>(root)
            .Where(b => b.Content is string content && content == label)
            .ToList();

        text.AppendLine($"可视化树里找到「{label}」按钮：{buttons.Count} 个");
        if (buttons.Count == 0)
        {
            text.AppendLine("  页面上所有按钮：" + string.Join("、",
                FindVisualChildren<Button>(root).Select(b => b.Content?.ToString() ?? "(无内容)")));
            return text.ToString();
        }

        foreach (var button in buttons)
        {
            text.AppendLine($"  Command={button.Command?.GetType().Name ?? "null"}" +
                            $"，CanExecute={button.Command?.CanExecute(null).ToString() ?? "n/a"}" +
                            $"，IsEnabled={button.IsEnabled}，IsVisible={button.IsVisible}" +
                            $"，尺寸={button.ActualWidth:0}×{button.ActualHeight:0}");
        }

        var target = buttons[0];
        try
        {
            var peer = new ButtonAutomationPeer(target);
            var invoke = (IInvokeProvider?)peer.GetPattern(PatternInterface.Invoke);
            if (invoke is null)
            {
                text.AppendLine("  拿不到 Invoke 模式，无法模拟点击。");
                return text.ToString();
            }

            invoke.Invoke();
            text.AppendLine("  已模拟点击。");
        }
        catch (Exception ex)
        {
            text.AppendLine("  点击时抛异常：" + ex);
        }

        return text.ToString();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// 界面自检用：找到计划表里「动作」那一列的下拉框，直接改选中项（等价于用户点开下拉选一条），
    /// 返回下拉框数量与改动前后的动作，用来确认双向绑定是通的。
    /// </summary>
    public string ChangeRowActionFromComboBox(int rowIndex = 0)
    {
        var text = new StringBuilder();
        var root = Content as DependencyObject ?? this;

        var combos = FindVisualChildren<ComboBox>(root)
            .Where(c => c.ItemsSource is IEnumerable<ActionChoice> && c.Visibility == Visibility.Visible)
            .ToList();
        text.AppendLine($"计划表里「动作」下拉框：{combos.Count} 个");
        if (combos.Count == 0) return text.ToString();

        var row = Math.Clamp(rowIndex, 0, combos.Count - 1);
        var combo = combos[row];
        var item = combo.DataContext as PlanItemViewModel;
        text.AppendLine($"  第 {row + 1} 个下拉框：{item?.DisplayName}（原动作 {item?.ActionText}）");
        text.AppendLine($"  ItemsSource={combo.Items.Count} 项，当前 SelectedValue={combo.SelectedValue}");

        // 选「卸载」：这一步走的是和用户点击完全一样的绑定通路
        combo.SelectedValue = PackageAction.Uninstall;
        text.AppendLine($"  设为卸载后：下拉框={combo.SelectedValue}，数据={item?.SelectedAction}，文案={item?.ActionText}");
        return text.ToString();
    }

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

        // 先写临时文件再替换：目标文件被看图工具或索引服务占用时，不至于让整轮截图失败。
        var temp = path + ".tmp";
        using (var stream = System.IO.File.Create(temp))
        {
            encoder.Save(stream);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                System.IO.File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(400);
            }
        }
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

    /// <summary>双击快照记录：在下面列出这次改了什么、每一项还能不能恢复。</summary>
    private async void Snapshots_OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.LoadRollbackDetailAsync();
    }

    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is PasswordBox box)
        {
            _viewModel.ApiKeyInput = box.Password;
        }
    }
}
