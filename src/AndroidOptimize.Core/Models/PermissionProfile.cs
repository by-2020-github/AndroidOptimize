namespace AndroidOptimize.Core.Models;

/// <summary>一条被命中的「值得警惕的权限」。</summary>
public sealed record PermissionHit(string Name, RiskLevel Risk, string Permission, string? Why);

/// <summary>一条被命中的权限组合。</summary>
public sealed record PermissionComboHit(string Name, RiskLevel Risk, IReadOnlyList<string> Matched, string? Why);

/// <summary>
/// 一个应用的「权限画像」：它申请了哪些值得警惕的权限。
///
/// 用途只有两个——在列表里看得见、能按权限筛出来。
/// 它**不参与自动勾选**：一个正常应用也可能申请悬浮窗（输入法的悬浮键盘、录屏工具），
/// 拿权限直接决定删不删，等于换一种瞎猜。
/// 它的价值是让用户在一堆不认识的包里，一眼找到最可疑的那几个。
/// </summary>
public sealed class AppPermissionProfile
{
    /// <summary>这台设备没给出权限列表时的占位（例如关掉了深度扫描，或 dumpsys 里没有这一段）。</summary>
    public static readonly AppPermissionProfile Unknown = new() { Known = false };

    /// <summary>应用申请的全部权限名。Known=false 时为空。</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>命中名单里 permissionRisks 的那些权限。顺序即名单里的顺序，高风险在前。</summary>
    public IReadOnlyList<PermissionHit> Hits { get; init; } = [];

    /// <summary>命中名单里 permissionCombos 的组合，例如「悬浮窗+安装应用」。</summary>
    public IReadOnlyList<PermissionComboHit> ComboHits { get; init; } = [];

    /// <summary>是否真的读到了权限列表。false = 未知，不能当成「这个应用很干净」。</summary>
    public bool Known { get; init; }

    /// <summary>命中项里的最高风险；没有任何命中就是 Low。</summary>
    public RiskLevel Level => Hits.Count == 0 ? RiskLevel.Low : Hits.Max(h => h.Risk);

    public bool HasHighRisk => Hits.Any(h => h.Risk == RiskLevel.High);

    /// <summary>中风险以上的命中项，高风险在前。低风险（例如「发送通知」）不进这个列表，否则整列都是噪音。</summary>
    public IReadOnlyList<PermissionHit> Notable =>
        Hits.Where(h => h.Risk != RiskLevel.Low).OrderByDescending(h => h.Risk).ToList();

    /// <summary>
    /// 排序用：**数值越小越可疑**，这样点一次「权限」列表头就是高危应用排在前面。
    /// 0 = 有高危权限，1 = 只有中风险，2 = 读了但没有值得警惕的权限，3 = 没读到。
    /// </summary>
    public int SortRank => !Known ? 3 : HasHighRisk ? 0 : Hits.Count > 0 ? 1 : 2;

    /// <summary>表格里那一列的内容。</summary>
    public string DisplayText
    {
        get
        {
            if (!Known) return "—";

            var notable = Notable;
            if (notable.Count == 0) return Hits.Count == 0 ? "无" : "—";

            // 列宽有限：两个名字 + 「等 N 项」。最常见的组合「悬浮窗·安装应用」正好整条显示出来。
            const int maxNames = 2;
            var text = string.Join("·", notable.Take(maxNames).Select(h => h.Name));
            return notable.Count > maxNames ? $"{text} 等{notable.Count}项" : text;
        }
    }

    /// <summary>鼠标悬停时的解释：为什么这些权限值得警惕，以及这个应用申请了什么。</summary>
    public string Summary
    {
        get
        {
            if (!Known)
            {
                return "没有读到这个应用的权限列表。\n" +
                       "权限要勾选「深度扫描第三方应用安装来源」后重新扫描才会读取。";
            }

            var lines = new List<string>();
            if (ComboHits.Count > 0)
            {
                lines.Add("危险组合：");
                foreach (var combo in ComboHits)
                {
                    var why = string.IsNullOrWhiteSpace(combo.Why) ? string.Empty : $"　{combo.Why}";
                    lines.Add($"· {combo.Name}{why}");
                }
                lines.Add(string.Empty);
            }

            if (Hits.Count > 0)
            {
                lines.Add("值得警惕的权限：");
                foreach (var hit in Hits)
                {
                    var why = string.IsNullOrWhiteSpace(hit.Why) ? string.Empty : $"　{hit.Why}";
                    lines.Add($"· {hit.Name}（{RiskText(hit.Risk)}）{hit.Permission}{why}");
                }
            }
            else
            {
                lines.Add("没有命中名单里任何一条值得警惕的权限。");
            }

            lines.Add(string.Empty);
            if (Permissions.Count == 0)
            {
                lines.Add("这个应用没有申请任何权限（很少见）。");
            }
            else
            {
                const int maxShown = 40;
                lines.Add($"申请的权限共 {Permissions.Count} 项：");
                lines.AddRange(Permissions.Take(maxShown).Select(p => "　" + p));
                if (Permissions.Count > maxShown) lines.Add($"　……还有 {Permissions.Count - maxShown} 项");
            }

            return string.Join("\n", lines);
        }
    }

    /// <summary>带上「这不是删除建议」的说明，用在只看得到这一项的地方。</summary>
    public string Tooltip => Summary + "\n\n这一列只用来帮你快速找到可疑应用，不会自动勾选任何东西。";

    private static string RiskText(RiskLevel risk) => risk switch
    {
        RiskLevel.High => "高",
        RiskLevel.Medium => "中",
        _ => "低",
    };
}

/// <summary>
/// 权限画像的生成与筛选。名单里的 permissionRisks 是唯一出处，
/// 改名单就能改这套判断，不需要改程序。
/// </summary>
public static class PermissionAdvice
{
    public const string FilterAll = "全部";
    public const string FilterHigh = "含高危权限";
    public const string FilterLow = "无高危权限";
    public const string FilterUnknown = "未读取";

    public static AppPermissionProfile Build(
        IReadOnlyList<string>? permissions,
        IReadOnlyList<PermissionRisk> risks,
        IReadOnlyList<PermissionCombo>? combos = null)
    {
        if (permissions is null) return AppPermissionProfile.Unknown;

        var hits = new List<PermissionHit>();
        foreach (var risk in risks)
        {
            if (string.IsNullOrWhiteSpace(risk.Match)) continue;

            // 权限名里带关键字就算命中。用 Contains 而不是全等，是为了同时覆盖
            // android.permission.X / com.vendor.permission.X / X 这些写法。
            var matched = permissions.FirstOrDefault(p =>
                p.Contains(risk.Match, StringComparison.OrdinalIgnoreCase));
            if (matched is null) continue;

            hits.Add(new PermissionHit(risk.Name, risk.Risk, matched, risk.Why));
        }

        var comboHits = new List<PermissionComboHit>();
        foreach (var combo in combos ?? [])
        {
            if (combo.AllOf.Count == 0) continue;

            var matched = new List<string>();
            var allMatched = true;
            foreach (var keyword in combo.AllOf)
            {
                var hit = permissions.FirstOrDefault(p =>
                    p.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (hit is null)
                {
                    allMatched = false;
                    break;
                }
                matched.Add(hit);
            }

            if (allMatched) comboHits.Add(new PermissionComboHit(combo.Name, combo.Risk, matched, combo.Why));
        }

        return new AppPermissionProfile
        {
            Permissions = permissions,
            Hits = hits,
            ComboHits = comboHits,
            Known = true,
        };
    }

    /// <summary>筛选下拉的内容：几个通用选项 + 名单里每一条具体权限。</summary>
    public static IReadOnlyList<string> FilterOptions(
        IReadOnlyList<PermissionRisk> risks,
        IReadOnlyList<PermissionCombo>? combos = null)
    {
        var options = new List<string> { FilterAll, FilterHigh, FilterLow, FilterUnknown };

        // 组合放在具体权限前面：单条权限命中太多，组合才是能直接下手的那一档。
        foreach (var name in (combos ?? []).Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            if (!options.Contains(name, StringComparer.Ordinal)) options.Add(name);
        }

        foreach (var name in risks.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            if (!options.Contains(name, StringComparer.Ordinal)) options.Add(name);
        }
        return options;
    }

    public static bool MatchesFilter(AppPermissionProfile profile, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter == FilterAll) return true;

        return filter switch
        {
            FilterHigh => profile.HasHighRisk,
            FilterLow => profile.Known && !profile.HasHighRisk,
            FilterUnknown => !profile.Known,
            // 组合名或具体权限名：命中才算
            _ => profile.ComboHits.Any(c => string.Equals(c.Name, filter, StringComparison.Ordinal))
                 || profile.Hits.Any(h => string.Equals(h.Name, filter, StringComparison.Ordinal)),
        };
    }
}
