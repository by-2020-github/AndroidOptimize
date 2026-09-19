namespace AndroidOptimize.Core.Models;

/// <summary>优化档位。数值越大包含的内容越多（Geek 档会同时包含 Normal 档的规则）。</summary>
public enum OptimizationTier
{
    /// <summary>只扫描不修改。</summary>
    ScanOnly = 0,
    /// <summary>一般优化：面向普通用户与老年用户，只处理广告、遥测、快应用、应用商店等。</summary>
    Normal = 1,
    /// <summary>极客模式：包含更多可选组件，逐项可勾选。</summary>
    Geek = 2,
    /// <summary>深度模式：包含高风险与低置信度条目，需要额外确认。</summary>
    Danger = 3,
}

public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
}

public enum PackageAction
{
    /// <summary>已评估确认不动。</summary>
    Keep = 0,
    /// <summary>停用（pm disable-user），可一键恢复。</summary>
    Disable = 1,
    /// <summary>为用户卸载（pm uninstall -k --user 0），保留数据，可通过 install-existing 恢复。</summary>
    Uninstall = 2,
    /// <summary>只做后台限制（待机分桶 + appops），不卸载不停用。</summary>
    Restrict = 3,
    /// <summary>修改系统设置项。</summary>
    Settings = 4,
}

public enum PackagePresence
{
    Unknown = 0,
    /// <summary>当前用户下已安装且启用。</summary>
    Installed = 1,
    /// <summary>已安装但被停用。</summary>
    Disabled = 2,
    /// <summary>在该用户下已被卸载（可通过 install-existing 恢复）。</summary>
    RemovedForUser = 3,
}

public enum PlanItemKind
{
    Package = 0,
    Setting = 1,
    /// <summary>由 AI 识别未知包产生的建议，永远默认不勾选。</summary>
    AiSuggestion = 2,
    /// <summary>由全局策略展开出来的项，例如批量关闭第三方应用的传感器权限。</summary>
    Policy = 3,
    /// <summary>
    /// 名单里查不到、也没有 AI 结论的第三方应用。
    /// 程序不知道它是干什么的，所以永远默认不勾选，只在用户主动点「选中未知应用」时才处理。
    /// </summary>
    Unknown = 4,
}

public enum ProtectionLevel
{
    None = 0,
    /// <summary>默认不勾选且 AI 不得建议，高级用户手动解锁后可操作。</summary>
    Guarded = 1,
    /// <summary>任何档位、任何来源都不得操作。</summary>
    Absolute = 2,
}

public enum ActionStatus
{
    Pending = 0,
    Success = 1,
    /// <summary>因为保护名单 / 未安装 / 置信度不足等原因没有执行。</summary>
    Skipped = 2,
    /// <summary>原动作失败，已降级为更保守的动作并成功。</summary>
    Downgraded = 3,
    Failed = 4,
}

public enum ListSource
{
    /// <summary>内置数据文件。</summary>
    Builtin = 0,
    /// <summary>程序目录下的 data 文件夹。</summary>
    AppFolder = 1,
    /// <summary>用户数据目录（后续在线更新写入这里）。</summary>
    UserData = 2,
}
