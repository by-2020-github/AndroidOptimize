using AndroidOptimize.App.Mvvm;
using AndroidOptimize.Core.Models;
using AndroidOptimize.Core.Services;

namespace AndroidOptimize.App.ViewModels;

/// <summary>
/// 回滚明细里的一行：这次动了什么、原状态是什么、还能不能恢复。
/// 「能不能恢复」要问手机（或看电脑上的备份），所以是异步填进来的，先显示「检查中…」。
/// </summary>
public sealed class RollbackItemViewModel : ObservableObject
{
    public required string DisplayName { get; init; }
    public required string Target { get; init; }
    public required string ActionText { get; init; }
    public required string StateText { get; init; }

    private string _restoreText = "检查中…";
    public string RestoreText { get => _restoreText; private set => SetProperty(ref _restoreText, value); }

    private Restorability _state = Restorability.Unknown;
    public Restorability State { get => _state; private set => SetProperty(ref _state, value); }

    /// <summary>需要连手机才能判断的项，出错时也要有个交代。</summary>
    public void SetResult(Restorability state, string text)
    {
        State = state;
        RestoreText = text;
    }

    public string RowTooltip => $"{DisplayName}（{Target}）\n动作：{ActionText}\n原状态：{StateText}\n恢复：{RestoreText}";
}
