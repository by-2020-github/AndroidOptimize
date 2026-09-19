namespace AndroidOptimize.App.ViewModels;

public sealed class PackageRowViewModel
{
    public required string PackageName { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public required string TypeText { get; init; }
    public required string StateText { get; init; }
    /// <summary>类型与状态合并成一列，避免列太窄被迫省略。</summary>
    public string TypeStateText => $"{TypeText}·{StateText}";

    /// <summary>鼠标悬停时显示的完整信息（表格放不下的安装来源与首次安装时间）。</summary>
    public string RowTooltip =>
        $"{PackageName}\n类型：{TypeText}\n状态：{StateText}\n安装来源：{InstallerText}\n首次安装：{FirstInstallText}\n{ListStateText}";

    public string InstallerText { get; init; } = "-";
    public string FirstInstallText { get; init; } = "-";
    public required string ListStateText { get; init; }
    public string NoteText { get; init; } = string.Empty;
    public bool IsThirdParty { get; init; }

    public string SearchText =>
        $"{PackageName} {DisplayName} {ListStateText} {InstallerText} {TypeText}".ToLowerInvariant();
}
