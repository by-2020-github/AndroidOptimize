using AndroidOptimize.Core.Ai;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.Core.Testing;

/// <summary>
/// 演示模式用的虚拟手机：一台装了典型国产预装软件与常见第三方应用的 Redmi 手机。
/// 用于功能预览、界面截图和培训，不连接真机、不修改任何东西。
/// </summary>
public static class DemoDevice
{
    public const string Serial = "DEMO0000";

    public static SimulatedAdbClient Create()
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["global/window_animation_scale"] = "1.0",
            ["global/transition_animation_scale"] = "1.0",
            ["global/animator_duration_scale"] = "1.0",
        };

        return new SimulatedAdbClient(Serial, Packages(), settings);
    }

    private static IEnumerable<SimulatedAdbClient.SimulatedPackage> Packages()
    {
        // 系统预装：广告、统计、快应用、应用商店、预装娱乐类
        yield return System("com.miui.systemAdSolution", "1.0.0");
        yield return System("com.miui.analytics", "1.0.0");
        yield return System("com.miui.uireporter", "1.0.0");
        yield return System("com.miui.bugreport", "1.0.0");
        yield return System("com.miui.hybrid", "1.0.0");
        yield return System("com.xiaomi.market", "4.8.2");
        yield return System("com.android.browser", "18.4.2");
        yield return System("com.miui.video", "2.6.1");
        yield return System("com.xiaomi.minigame", "1.0.0");
        yield return System("com.xiaomi.migameservice", "1.0.0");
        yield return System("com.mfashiongallery.emag", "1.0.0");
        yield return System("com.miui.cleanmaster", "1.0.0");
        yield return System("com.miui.contentcatcher", "1.0.0");
        yield return System("com.miui.voicetrigger", "1.0.0");
        yield return System("com.miui.voiceassist", "6.3.1");
        yield return System("com.xiaomi.aireco", "1.0.0");
        yield return System("com.android.updater", "4.3.1");
        yield return System("com.miui.player", "1.0.0");

        // 受保护的系统组件（截图里应当显示为"受保护"）
        yield return System("com.miui.guardprovider", "1.0.0");
        yield return System("com.miui.securitycenter", "8.2.1");
        yield return System("com.android.systemui", "14");
        yield return System("com.android.settings", "14");
        yield return System("com.android.phone", "14");
        yield return System("com.android.contacts", "14");
        yield return System("com.android.mms", "14");
        yield return System("com.miui.home", "4.5.6");
        yield return System("com.android.packageinstaller", "14");
        yield return System("com.baidu.input_mi", "8.6.2");
        yield return System("com.miui.gallery", "3.6.2");
        yield return System("com.android.camera", "5.1.0");
        yield return System("com.miui.tsmclient", "1.0.0");
        yield return System("com.hicorenational.antifraud", "1.0.0");
        yield return System("com.android.vending", "39.2.1");

        // 第三方应用
        yield return Third("com.tencent.mm", "8.0.48", "com.xiaomi.market", "2024-03-11 20:14:02");
        yield return Third("com.tencent.mobileqq", "8.9.85", "com.xiaomi.market", "2024-03-11 20:16:41");
        yield return Third("com.eg.android.AlipayGphone", "10.5.60", "com.xiaomi.market", "2024-03-12 09:02:15");
        yield return Third("com.ss.android.ugc.aweme", "29.6.0", "com.xiaomi.market", "2025-12-18 19:41:08");
        yield return Third("com.ss.android.ugc.aweme.lite", "26.9.0", "com.xiaomi.market", "2026-01-07 21:33:52");
        yield return Third("com.xunmeng.pinduoduo", "7.28.0", "com.xiaomi.market", "2026-02-14 10:22:37");
        yield return Third("com.taobao.taobao", "10.38.30", "com.xiaomi.market", "2025-11-03 15:08:19");
        yield return Third("com.jingdong.app.mall", "14.2.6", "com.xiaomi.market", "2025-11-03 15:21:44");
        yield return Third("com.smile.gifmaker", "11.8.20", "com.xiaomi.market", "2026-03-02 08:55:12");
        yield return Third("com.xingin.xhs", "8.42.0", "com.xiaomi.market", "2026-01-19 22:07:31");
        yield return Third("com.sankuai.meituan", "12.14.203", "com.xiaomi.market", "2025-09-27 11:40:06");
        yield return Third("com.autonavi.minimap", "13.18.0", "com.xiaomi.market", "2024-05-02 14:33:58");
        yield return Third("cn.wps.moffice_eng", "14.6.1", "com.android.packageinstaller", "2024-08-15 16:20:11");
        yield return Third("com.icbc", "9.0.1.2", "com.android.packageinstaller", "2025-04-22 09:12:47");

        // 名单里没有记录的应用：用来展示 AI 识别建议
        yield return Third("com.qihoo.appstore", "8.0.62", "com.android.packageinstaller", "2026-08-30 21:15:33", neverLaunched: true);
        yield return Third("com.sohu.sohuvideo", "7.2.10", "com.android.packageinstaller", "2026-09-02 19:48:20", neverLaunched: true);
        yield return Third("com.baidu.searchbox", "14.30.0", "com.xiaomi.market", "2026-08-21 07:26:14", neverLaunched: true);

        // 被莫名装上一堆、而且一次都没被打开过的应用：用来演示「一键选中从未打开过的应用」
        yield return Third("com.lucky.wifi", "1.4.2", "com.xiaomi.market", "2026-09-15 22:41:07", neverLaunched: true);
        yield return Third("com.clean.master", "3.2.8", "com.xiaomi.market", "2026-09-15 22:41:12", neverLaunched: true);
        yield return Third("com.qihoo.browser", "9.1.0", "com.android.packageinstaller", "2026-09-16 08:12:55", neverLaunched: true);
        yield return Third("com.dh.tiantian", "2.6.1", "com.xiaomi.market", "2026-09-16 08:13:04", neverLaunched: true);
        yield return Third("com.yidian.zheli", "5.0.4", "com.xiaomi.market", "2026-09-17 19:26:31", neverLaunched: true);

        // 名单外但使用者确实打开过的应用：演示时不会被「一键选中从未打开过的」勾上
        yield return Third("com.tencent.qqlive", "8.12.5", "com.xiaomi.market", "2025-12-29 20:52:09", neverLaunched: false);
        yield return Third("tv.danmaku.bili", "8.40.0", "com.xiaomi.market", "2025-10-11 19:05:27", neverLaunched: false);
    }

    private static SimulatedAdbClient.SimulatedPackage System(string name, string version) =>
        new(name, System: true, VersionName: version, FirstInstall: "2024-01-01 00:00:00");

    private static SimulatedAdbClient.SimulatedPackage Third(
        string name, string version, string installer, string firstInstall, bool? neverLaunched = null) =>
        new(name, System: false, VersionName: version, Installer: installer, FirstInstall: firstInstall, NeverLaunched: neverLaunched);
}

/// <summary>
/// 演示模式下的 AI 识别结果。只对演示设备里那几个"名单外"的应用返回固定答案，
/// 用来展示 AI 建议长什么样，不会真的联网。
/// </summary>
public sealed class DemoClassifier : IPackageClassifier
{
    private static readonly Dictionary<string, PackageClassification> Answers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["com.qihoo.appstore"] = new()
        {
            PackageName = "com.qihoo.appstore", Category = "应用分发", Action = PackageAction.Disable,
            Confidence = 0.88, Reason = "360 手机助手，第三方应用分发入口，推送安装较多", Source = "ai",
        },
        ["com.sohu.sohuvideo"] = new()
        {
            PackageName = "com.sohu.sohuvideo", Category = "预装应用", Action = PackageAction.Uninstall,
            Confidence = 0.84, Reason = "搜狐视频，含开屏广告与后台推荐流", Source = "ai",
        },
        ["com.baidu.searchbox"] = new()
        {
            PackageName = "com.baidu.searchbox", Category = "推荐与内容流", Action = PackageAction.Disable,
            Confidence = 0.81, Reason = "百度搜索，常驻后台推送热榜与资讯", Source = "ai",
        },
    };

    public bool IsAvailable => true;

    public async Task<IReadOnlyList<PackageClassification>> ClassifyAsync(
        IReadOnlyList<UnknownPackage> packages,
        DeviceInfo device,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // 模拟一点网络耗时，让界面上的进度提示看起来真实。
        progress?.Report("AI 识别未知名应用…（演示模式）");
        await Task.Delay(400, ct).ConfigureAwait(false);

        return packages
            .Where(p => Answers.ContainsKey(p.PackageName))
            .Select(p => Answers[p.PackageName])
            .ToList();
    }
}
