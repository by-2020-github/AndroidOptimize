using AndroidOptimize.App.Mvvm;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.App.ViewModels;

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

    public bool IsSelectable => Model.IsSelectable;

    /// <summary>外部直接改了 Model.IsSelected 之后，只刷新这一行的显示（不触发汇总重算）。</summary>
    public void RaiseIsSelectedChanged() => OnPropertyChanged(nameof(IsSelected));
    public string DisplayName => Model.DisplayName;
    public string Target => Model.Target;
    public string Category => Model.Category;
    public string ActionText => Model.ActionText;
    public string RiskText => Model.RiskText;
    public string TierText => Model.TierText;
    public string SourceText => Model.SourceText;
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
