namespace AndroidOptimize.Core.Models;

/// <summary>建议的分组，用于界面筛选。</summary>
public enum AdviceGroup
{
    /// <summary>名册说可以安全移除，而且用户打不开它——默认已勾选。</summary>
    Clean = 0,
    /// <summary>名册说可以移除，但需要先确认。</summary>
    Optional = 1,
    /// <summary>不该动：受保护名单，或名册明确说不建议动。</summary>
    Keep = 2,
    /// <summary>名单里有明确规则要处理。</summary>
    Listed = 3,
    /// <summary>名单和名册里都查不到，只能自己判断。</summary>
    Unknown = 4,
    /// <summary>设置项、策略项这类不是「应用」的条目。</summary>
    Other = 5,
}

/// <summary>
/// 「这条该怎么处理」的唯一出处。
///
/// 界面上两处列表（手机体检、优化计划）都用它，保证同一行在哪儿看都是同一句话；
/// 计划引擎的默认勾选也走 <see cref="RemovalAdvice"/>，不会出现「建议说可以清、却没勾」的矛盾。
///
/// 注意这里**没有白名单/黑名单**：每一条依据都是「名单里的明确规则」或「名册里的明确结论」，
/// 拿不出依据的就说「名单外·自行判断」，交给用户。
/// </summary>
public static class Advice
{
    public const string TextClean = "UAD·建议清理";
    public const string TextOptional = "UAD·可清理";
    public const string TextCatalogKeep = "UAD·不建议动";
    public const string TextCatalogUnsafe = "UAD·不要动";
    public const string TextProtectedStop = "受保护·不要动";
    public const string TextProtectedKeep = "受保护·默认留";
    public const string TextListedHandle = "名单·建议处理";
    public const string TextListedOptional = "名单·按需处理";
    public const string TextListedCareful = "名单·谨慎处理";
    public const string TextListedKeep = "名单·保留";
    public const string TextUnknown = "名单外·自行判断";
    public const string TextSetting = "设置项";
    public const string TextPolicy = "策略·批量限制";
    public const string TextAi = "AI·建议处理";

    /// <summary>体检表用：根据规则、保护名单、名册三者算出建议。</summary>
    public static (string Text, AdviceGroup Group) ForPackage(
        RuleRecord? rule, ProtectionMatch protection, CatalogEntry? catalog, bool? hasLauncher,
        PackageClassification? ai = null)
    {
        if (protection.Level == ProtectionLevel.Absolute) return (TextProtectedStop, AdviceGroup.Keep);
        if (protection.Level == ProtectionLevel.Guarded) return (TextProtectedKeep, AdviceGroup.Keep);

        if (rule is not null)
        {
            if (rule.Action == PackageAction.Keep) return (TextListedKeep, AdviceGroup.Listed);

            return rule.Tier switch
            {
                OptimizationTier.Normal => (TextListedHandle, AdviceGroup.Listed),
                OptimizationTier.Geek => (TextListedOptional, AdviceGroup.Listed),
                _ => (TextListedCareful, AdviceGroup.Keep),
            };
        }

        if (catalog is not null)
        {
            var text = RemovalAdvice.TextFor(catalog.Removal, hasLauncher);
            var group = RemovalAdvice.Normalize(catalog.Removal) switch
            {
                RemovalAdvice.Recommended when hasLauncher == false => AdviceGroup.Clean,
                RemovalAdvice.Recommended or RemovalAdvice.Advanced => AdviceGroup.Optional,
                _ => AdviceGroup.Keep,
            };
            return (text, group);
        }

        // 名册里也没有，但 AI 给过结论（名单外的应用才有这一步）
        if (ai is not null && ai.Action != PackageAction.Keep)
        {
            return (TextAi, AdviceGroup.Unknown);
        }

        return (TextUnknown, AdviceGroup.Unknown);
    }

    /// <summary>优化计划用：计划条目上已经带齐了判断依据，直接推。</summary>
    public static (string Text, AdviceGroup Group) ForPlanItem(PlanItem item) => item.Kind switch
    {
        PlanItemKind.Setting => (TextSetting, AdviceGroup.Other),
        PlanItemKind.Policy => (TextPolicy, AdviceGroup.Other),
        PlanItemKind.AiSuggestion => (TextAi, AdviceGroup.Unknown),
        PlanItemKind.Unknown => (TextUnknown, AdviceGroup.Unknown),
        PlanItemKind.Catalog => ForCatalog(item.Removal, item.HasLauncher),
        _ => item.Action switch
        {
            PackageAction.Keep => (TextListedKeep, AdviceGroup.Listed),
            _ => item.Tier switch
            {
                OptimizationTier.Normal => (TextListedHandle, AdviceGroup.Listed),
                OptimizationTier.Geek => (TextListedOptional, AdviceGroup.Listed),
                _ => (TextListedCareful, AdviceGroup.Keep),
            },
        },
    };

    private static (string Text, AdviceGroup Group) ForCatalog(string? removal, bool? hasLauncher)
    {
        var text = RemovalAdvice.TextFor(removal, hasLauncher);
        var group = RemovalAdvice.Normalize(removal) switch
        {
            RemovalAdvice.Recommended when hasLauncher == false => AdviceGroup.Clean,
            RemovalAdvice.Recommended or RemovalAdvice.Advanced => AdviceGroup.Optional,
            _ => AdviceGroup.Keep,
        };
        return (text, group);
    }

    public static string GroupName(AdviceGroup group) => group switch
    {
        AdviceGroup.Clean => "建议清理",
        AdviceGroup.Optional => "可清理",
        AdviceGroup.Keep => "不要动",
        AdviceGroup.Listed => "名单处理",
        AdviceGroup.Unknown => "名单外",
        _ => "其它",
    };

    /// <summary>筛选下拉的选项，顺序即展示顺序。</summary>
    public static IReadOnlyList<string> FilterOptions { get; } =
    [
        "全部",
        "建议清理",
        "可清理",
        "不要动",
        "名单处理",
        "名单外",
        "其它",
    ];

    public static bool MatchesFilter(AdviceGroup group, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || filter == "全部" || GroupName(group) == filter;
}
