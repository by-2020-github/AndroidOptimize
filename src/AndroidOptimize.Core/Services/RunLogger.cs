using System.Text;

namespace AndroidOptimize.Core.Services;

/// <summary>把每一步操作都落到日志文件，方便出问题时让用户把日志发过来。</summary>
public sealed class RunLogger : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public RunLogger(string? path = null)
    {
        Path = path ?? AppPaths.NewLogPath();
        AppPaths.EnsureCreated();
        try
        {
            _writer = new StreamWriter(Path, append: false, Encoding.UTF8) { AutoFlush = true };
        }
        catch
        {
            Path = string.Empty;
        }
    }

    public string Path { get; private set; }
    public event Action<string>? LineWritten;

    public void Info(string message) => Write("信息", message);
    public void Warn(string message) => Write("提示", message);
    public void Error(string message) => Write("错误", message);
    public void Step(string message) => Write("步骤", message);
    public void Adb(string message) => Write("ADB", message);
    public void Success(string message) => Write("成功", message);

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        lock (_gate)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // 日志写失败不能影响主流程。
            }
        }
        LineWritten?.Invoke(line);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
