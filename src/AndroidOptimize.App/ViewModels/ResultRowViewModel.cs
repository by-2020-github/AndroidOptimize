using AndroidOptimize.Core.Models;

namespace AndroidOptimize.App.ViewModels;

public sealed class ResultRowViewModel
{
    public required int Index { get; init; }
    public required string DisplayName { get; init; }
    public required string Target { get; init; }
    public required string ActionText { get; init; }
    public required string StatusText { get; init; }
    public required string VerifyText { get; init; }
    public required string Message { get; init; }
    public string? Hint { get; init; }
    public required ActionStatus Status { get; init; }

    public string Tooltip
    {
        get
        {
            var parts = new List<string> { Message };
            if (!string.IsNullOrWhiteSpace(Hint)) parts.Add($"建议：{Hint}");
            return string.Join("\n", parts);
        }
    }
}
