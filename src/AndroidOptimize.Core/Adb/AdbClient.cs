using System.Diagnostics;
using System.Text;

namespace AndroidOptimize.Core.Adb;

/// <summary>
/// 对 adb.exe 的薄封装。带超时、UTF-8 输出、无控制台窗口。
/// 约定：任何 adb 调用都必须带超时，避免设备未授权时界面永久卡住。
/// </summary>
public class AdbClient
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public AdbClient(string adbPath, string? serial = null, Action<string>? trace = null)
    {
        AdbPath = adbPath;
        Serial = serial;
        _trace = trace;
    }

    private readonly Action<string>? _trace;

    public string AdbPath { get; }
    public string? Serial { get; protected set; }

    public AdbClient ForDevice(string serial) => new(AdbPath, serial, _trace);

    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan LongTimeout { get; } = TimeSpan.FromMinutes(3);

    public Task<AdbResult> RunAsync(string argument, TimeSpan? timeout = null, CancellationToken ct = default)
        => RunAsync([argument], timeout, ct);

    public async Task<AdbResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var args = new List<string>(arguments.Count + 2);
        if (!string.IsNullOrEmpty(Serial))
        {
            args.Add("-s");
            args.Add(Serial);
        }
        args.AddRange(arguments);
        return await RunRawAsync(args, timeout ?? DefaultTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>执行设备端 shell 命令。整条命令作为单个参数传递，由设备端 shell 解析。</summary>
    public Task<AdbResult> ShellAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
        => RunAsync(["shell", command], timeout, ct);

    public virtual async Task<AdbResult> RunRawAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var commandLine = "adb " + string.Join(' ', arguments.Select(Quote));
        _trace?.Invoke($"> {commandLine}");

        var psi = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
            {
                throw new AdbException($"无法启动 adb：{AdbPath}");
            }
        }
        catch (Exception ex) when (ex is not AdbException)
        {
            throw new AdbException($"无法启动 adb（{AdbPath}）：{ex.Message}", null, ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var timedOut = false;

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            TryKill(process);
            if (!timedOut)
            {
                throw;
            }
        }

        var stdout = await SafeAwait(stdoutTask).ConfigureAwait(false);
        var stderr = await SafeAwait(stderrTask).ConfigureAwait(false);
        stopwatch.Stop();

        var exitCode = timedOut ? -1 : SafeExitCode(process);
        var result = new AdbResult
        {
            CommandLine = commandLine,
            ExitCode = exitCode,
            StdOut = stdout.TrimEnd(),
            StdErr = timedOut
                ? $"命令超过 {timeout.TotalSeconds:0} 秒未返回，已中止。请确认手机已解锁并允许 USB 调试。"
                : stderr.TrimEnd(),
            Duration = stopwatch.Elapsed,
            TimedOut = timedOut,
        };

        _trace?.Invoke($"< exit={result.ExitCode} {stopwatch.ElapsedMilliseconds}ms {Trim(result.StdOut)}{Trim(result.StdErr)}");
        return result;
    }

    public async Task<string> ShellTextAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var result = await ShellAsync(command, timeout, ct).ConfigureAwait(false);
        return result.StdOut;
    }

    /// <summary>
    /// 用 exec-out 跑一条设备端命令，并把 stdout 当**原始字节**取回来。
    /// 读 APK 这类二进制内容必须走这里：普通 shell 会做换行转换，二进制会被破坏。
    /// </summary>
    public virtual async Task<byte[]> ExecOutBytesAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(Serial))
        {
            args.Add("-s");
            args.Add(Serial);
        }
        args.Add("exec-out");
        args.Add(command);

        _trace?.Invoke($"> adb exec-out {command}");

        var psi = new ProcessStartInfo(AdbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Utf8NoBom,
        };
        foreach (var argument in args)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return [];
        }
        catch
        {
            return [];
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var copyTask = CopyToMemoryAsync(process.StandardOutput.BaseStream, ct);
        var drainTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (!ct.IsCancellationRequested) return [];
            throw;
        }

        var data = await SafeAwaitBytes(copyTask).ConfigureAwait(false);
        await SafeAwait(drainTask).ConfigureAwait(false);
        _trace?.Invoke($"< exec-out {data.Length} 字节");
        return data;
    }

    private static async Task<byte[]> CopyToMemoryAsync(Stream source, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, ct).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static async Task<byte[]> SafeAwaitBytes(Task<byte[]> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 忽略：进程可能已经退出。
        }
    }

    private static string Quote(string value) =>
        value.Contains(' ') ? $"\"{value}\"" : value;

    private static string Trim(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length > 160 ? flat[..160] + "…" : flat;
    }
}
