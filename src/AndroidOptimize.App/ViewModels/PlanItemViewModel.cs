using AndroidOptimize.App.Mvvm;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.App.ViewModels;

/// <summary>动作下拉里的一项：值是枚举，显示是中文。</summary>
public sealed record ActionChoice(PackageAction Value, string Label);

public sealed class PlanItemViewModel : ObservableObject
{
    public PlanItemViewModel(PlanItem model)
    {
        Model = model;
        _selectedChoice = model.SettingValue;
    }

    private string? _selectedChoice;

    public PlanItem Model { get; }
    public event Action? SelectionChanged;
    /// <summary>用户在这一行改了动作（停用 ↔ 卸载），需要刷新汇总。</summary>
    public event Action? ActionChanged;

    public bool IsSelected
    {
        get => Model.IsSelected;
        set
        {
            if (Model.IsSelected == value) return;
            Model.IsSelected = value;
            OnPropertyChanged();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>批量改动作时用：外部直接改了 Model 之后刷新这一行。</summary>
    public void RaiseActionChanged()
    {
        OnPropertyChanged(nameof(SelectedAction));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(HasActionOverride));
        OnPropertyChanged(nameof(RowTooltip));
    }

    public bool IsSelectable => Model.IsSelectable;

    /// <summary>外部直接改了 Model.IsSelected 之后，只刷新这一行的显示（不触发汇总重算）。</summary>
    public void RaiseIsSelectedChanged() => OnPropertyChanged(nameof(IsSelected));
    public string DisplayName => Model.DisplayName;
    public string Target => Model.Target;
    public string Category => Model.Category;
    public string ActionText => Model.ActionText;

    /// <summary>能不能自己选停用/卸载（设置项、后台限制这类不行）。</summary>
    public bool CanChooseAction => Model.CanChooseAction;

    /// <summary>下拉里的两项。用中文标签显示，不能直接拿枚举给用户看。</summary>
    public IReadOnlyList<ActionChoice> ActionOptions { get; } =
    [
        new ActionChoice(PackageAction.Disable, "停用"),
        new ActionChoice(PackageAction.Uninstall, "卸载"),
    ];

    /// <summary>
    /// 这一行实际会执行的动作。用户改动时记住「手动指定」，选回默认值就把覆盖清掉，
    /// 这样界面上永远能看出哪些是名单给的、哪些是人改的。
    /// </summary>
    public PackageAction SelectedAction
    {
        get => Model.EffectiveAction;
        set
        {
            if (Model.EffectiveAction == value) return;

            Model.ActionOverride = value == Model.Action ? null : value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionText));
            OnPropertyChanged(nameof(RowTooltip));
            ActionChanged?.Invoke();
        }
    }

    /// <summary>是不是用户手动指定的动作（列表里用加粗/颜色区分）。</summary>
    public bool HasActionOverride => Model.HasActionOverride;
    public string TypeText => Model.TypeText;
    public string RowTooltip => Model.RowTooltip;
    public string RiskText => Model.RiskText;
    public string TierText => Model.TierText;
    public string SourceText => Model.SourceText;
    /// <summary>权限画像：申请了哪些值得警惕的权限。只用于展示与筛选。</summary>
    public AppPermissionProfile PermissionProfile => Model.PermissionProfile;
    public string PermissionText => Model.PermissionProfile.DisplayText;
    public RiskLevel PermissionLevel => Model.PermissionProfile.Level;
    /// <summary>排序用：数值越小越可疑，第一次点「权限」列就是高危在前。</summary>
    public int PermissionSortRank => Model.PermissionProfile.SortRank;
    /// <summary>和手机体检表用同一套「建议」说法。</summary>
    public string AdviceText => Advice.ForPlanItem(Model).Text;
    public AdviceGroup AdviceGroup => Advice.ForPlanItem(Model).Group;
    public string? Reason => Model.Reason;
    public string? Impact => Model.Impact;
    public PackageAction Action => Model.Action;
    public RiskLevel Risk => Model.Risk;
    public PlanItemKind Kind => Model.Kind;
    public double Confidence => Model.Confidence;

    public string ConfidenceText => $"{Model.Confidence * 100:0}%";

    public bool IsSetting => Model.Kind == PlanItemKind.Setting;
    public bool HasChoices => Model.Choices is { Count: > 0 };
    public IReadOnlyList<SettingChoice>? Choices => Model.Choices;

    public string? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (!SetProperty(ref _selectedChoice, value)) return;
            Model.SettingValue = value;
            OnPropertyChanged(nameof(SettingValueText));
        }
    }

    public string SettingValueText => Model.SettingValue ?? "-";

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Model.Reason)) parts.Add(Model.Reason!);
            if (!string.IsNullOrWhiteSpace(Model.Impact)) parts.Add($"影响：{Model.Impact}");
            return string.Join("  ", parts);
        }
    }
}
