namespace AndroidOptimize.Core.Adb;

public sealed record AdbResult
{
    public required string CommandLine { get; init; }
    public required int ExitCode { get; init; }
    public required string StdOut { get; init; }
    public required string StdErr { get; init; }
    public required TimeSpan Duration { get; init; }
    public bool TimedOut { get; init; }

    public bool Ok => ExitCode == 0 && !TimedOut;

    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}\n{StdErr}";

    public string[] Lines =>
        Combined.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>设备端 shell 常见的中英文失败提示。</summary>
    public bool LooksLikeFailure
    {
        get
        {
            var text = Combined;
            return text.Contains("Failure", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Error:", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Unknown package", StringComparison.OrdinalIgnoreCase)
                || text.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
                || text.Contains("失败")
                || text.Contains("权限拒绝");
        }
    }
}

public sealed class AdbException : Exception
{
    public AdbException(string message, AdbResult? result = null, Exception? inner = null)
        : base(message, inner)
    {
        Result = result;
    }

    public AdbResult? Result { get; }
}
