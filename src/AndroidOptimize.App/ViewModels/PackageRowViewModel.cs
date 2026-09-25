using AndroidOptimize.Core.Models;

namespace AndroidOptimize.App.ViewModels;

public sealed class PackageRowViewModel
{
    public required string PackageName { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public required string TypeText { get; init; }
    public required string StateText { get; init; }
    /// <summary>推荐怎么处理：不要动 / 建议处理 / 按需处理 / 谨慎处理 / 未知，自行判断。</summary>
    public string AdviceText { get; init; } = string.Empty;
    public AdviceGroup AdviceGroup { get; init; }
    /// <summary>类型与状态合并成一列，避免列太窄被迫省略。</summary>
    public string TypeStateText => $"{TypeText}·{StateText}";

    /// <summary>鼠标悬停时显示的完整信息（表格放不下的安装来源与首次安装时间）。</summary>
    public string RowTooltip
    {
        get
        {
            var text =
                $"{PackageName}\n名称：{(string.IsNullOrWhiteSpace(DisplayName) ? "（读不到）" : DisplayName)}\n" +
                $"类型：{TypeText}\n状态：{StateText}\n建议：{AdviceText}\n" +
                $"安装来源：{InstallerText}\n首次安装：{FirstInstallText}\n{ListStateText}\n\n" +
                $"权限：{PermissionProfile.DisplayText}\n{PermissionProfile.Summary}";
            return text;
        }
    }

    public string InstallerText { get; init; } = "-";
    public string FirstInstallText { get; init; } = "-";
    public required string ListStateText { get; init; }
    public string NoteText { get; init; } = string.Empty;
    public bool IsThirdParty { get; init; }
    public bool IsSystem => !IsThirdParty;

    /// <summary>权限画像：申请了哪些值得警惕的权限。只用于展示与筛选。</summary>
    public AppPermissionProfile PermissionProfile { get; init; } = AppPermissionProfile.Unknown;

    public string PermissionText => PermissionProfile.DisplayText;
    public RiskLevel PermissionLevel => PermissionProfile.Level;

    /// <summary>排序用：数值越小越可疑，第一次点「权限」列就是高危在前。</summary>
    public int PermissionSortRank => PermissionProfile.SortRank;

    public string SearchText =>
        $"{PackageName} {DisplayName} {ListStateText} {InstallerText} {TypeText} {AdviceText} {NoteText} {PermissionText}"
            .ToLowerInvariant();
}
