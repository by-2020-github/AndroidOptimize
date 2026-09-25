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

        var packages = Packages().ToList();
        return new SimulatedAdbClient(Serial, packages, settings)
        {
            Labels = packages
                .Where(p => !string.IsNullOrWhiteSpace(p.Label))
                .ToDictionary(p => p.Name, p => p.Label!, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static IEnumerable<SimulatedAdbClient.SimulatedPackage> Packages()
    {
        // 权限用真机上常见的写法（android.permission.X）填，好让「权限」列和筛选有东西可看。
        static IReadOnlyList<string> P(params string[] names) => names;

        // 系统预装：广告、统计、快应用、应用商店、预装娱乐类
        yield return System("com.miui.systemAdSolution", "1.0.0",
            P("android.permission.SYSTEM_ALERT_WINDOW", "android.permission.REQUEST_INSTALL_PACKAGES",
              "android.permission.QUERY_ALL_PACKAGES", "android.permission.POST_NOTIFICATIONS"));
        yield return System("com.miui.analytics", "1.0.0",
            P("android.permission.PACKAGE_USAGE_STATS", "android.permission.READ_PHONE_STATE",
              "android.permission.RECEIVE_BOOT_COMPLETED"));
        yield return System("com.miui.uireporter", "1.0.0");
        yield return System("com.miui.bugreport", "1.0.0");
        yield return System("com.miui.hybrid", "1.0.0",
            P("android.permission.SYSTEM_ALERT_WINDOW", "com.android.launcher.permission.INSTALL_SHORTCUT",
              "android.permission.QUERY_ALL_PACKAGES"));
        yield return System("com.xiaomi.market", "4.8.2",
            P("android.permission.REQUEST_INSTALL_PACKAGES", "android.permission.QUERY_ALL_PACKAGES",
              "android.permission.REQUEST_DELETE_PACKAGES"));
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

        // 名册收录、但 packages.json 没写规则的系统组件：
        // 用来演示「扫完就有结论、建议清理的已自动勾选」
        yield return SystemLabeled("com.miui.misightservice", "1.0.0", "MIUI 数据洞察");
        yield return SystemLabeled("com.xiaomi.aiservice", "2.1.0", "小米 AI 引擎");
        yield return SystemLabeled("com.miui.cotaservice", "1.0.0", "配置更新服务");
        yield return SystemLabeled("com.xiaomi.finddevice", "3.0.1", "查找设备");

        // 常用第三方应用（名单里已有规则）
        // 微信刻意什么高危权限都不给：权限列不应该把熟人常用的应用标成可疑。
        yield return Third("com.tencent.mm", "8.0.48", "com.xiaomi.market", "2024-03-11 20:14:02", label: "微信",
            permissions: P("android.permission.INTERNET", "android.permission.CAMERA",
                           "android.permission.RECORD_AUDIO", "android.permission.ACCESS_NETWORK_STATE"));
        yield return Third("com.tencent.mobileqq", "8.9.85", "com.xiaomi.market", "2024-03-11 20:16:41", label: "QQ");
        yield return Third("com.eg.android.AlipayGphone", "10.5.60", "com.xiaomi.market", "2024-03-12 09:02:15", label: "支付宝");
        yield return Third("com.ss.android.ugc.aweme", "29.6.0", "com.xiaomi.market", "2025-12-18 19:41:08", label: "抖音",
            permissions: P("android.permission.REQUEST_INSTALL_PACKAGES", "android.permission.QUERY_ALL_PACKAGES",
                           "android.permission.READ_PHONE_STATE"));
        yield return Third("com.ss.android.ugc.aweme.lite", "26.9.0", "com.xiaomi.market", "2026-01-07 21:33:52", label: "抖音极速版");
        yield return Third("com.xunmeng.pinduoduo", "7.28.0", "com.xiaomi.market", "2026-02-14 10:22:37", label: "拼多多");
        yield return Third("com.taobao.taobao", "10.38.30", "com.xiaomi.market", "2025-11-03 15:08:19", label: "淘宝",
            permissions: P("android.permission.QUERY_ALL_PACKAGES", "android.permission.READ_PHONE_STATE",
                           "android.permission.INSTALL_SHORTCUT"));
        yield return Third("com.jingdong.app.mall", "14.2.6", "com.xiaomi.market", "2025-11-03 15:21:44", label: "京东");
        yield return Third("com.smile.gifmaker", "11.8.20", "com.xiaomi.market", "2026-03-02 08:55:12", label: "快手");

        // 名单外、但属于「正常应用」的（会被白名单保留，名字由 APK 读出）
        yield return Third("com.xingin.xhs", "8.42.0", "com.xiaomi.market", "2026-01-19 22:07:31", label: "小红书");
        yield return Third("com.sankuai.meituan", "12.14.203", "com.xiaomi.market", "2025-09-27 11:40:06", label: "美团");
        yield return Third("com.autonavi.minimap", "13.18.0", "com.xiaomi.market", "2024-05-02 14:33:58", label: "高德地图",
            permissions: P("android.permission.ACCESS_FINE_LOCATION", "android.permission.ACCESS_BACKGROUND_LOCATION",
                           "android.permission.INTERNET"));
        yield return Third("cn.wps.moffice_eng", "14.6.1", "com.android.packageinstaller", "2024-08-15 16:20:11", label: "WPS Office");
        yield return Third("com.icbc", "9.0.1.2", "com.android.packageinstaller", "2025-04-22 09:12:47", label: "中国工商银行",
            permissions: P("android.permission.INTERNET", "android.permission.ACCESS_NETWORK_STATE"));

        // 名单外、有 AI 识别结论的
        yield return Third("com.qihoo.appstore", "8.0.62", "com.android.packageinstaller", "2026-08-30 21:15:33", neverLaunched: true, label: "360 手机助手",
            permissions: P("android.permission.SYSTEM_ALERT_WINDOW", "android.permission.REQUEST_INSTALL_PACKAGES",
                           "android.permission.QUERY_ALL_PACKAGES"));
        yield return Third("com.sohu.sohuvideo", "7.2.10", "com.android.packageinstaller", "2026-09-02 19:48:20", neverLaunched: true, label: "搜狐视频",
            permissions: P("android.permission.RECEIVE_BOOT_COMPLETED", "android.permission.READ_PHONE_STATE"));
        yield return Third("com.baidu.searchbox", "14.30.0", "com.xiaomi.market", "2026-08-21 07:26:14", neverLaunched: true, label: "百度",
            permissions: P("android.permission.QUERY_ALL_PACKAGES", "android.permission.RECEIVE_BOOT_COMPLETED",
                           "android.permission.READ_PHONE_STATE"));

        // 被莫名装上一堆、而且一次都没被打开过的应用：演示「懒人模式」要清掉的对象
        yield return Third("com.lucky.wifi", "1.4.2", "com.xiaomi.market", "2026-09-15 22:41:07", neverLaunched: true, label: "WiFi 万能钥匙",
            permissions: P("android.permission.SYSTEM_ALERT_WINDOW", "android.permission.ACCESS_FINE_LOCATION",
                           "android.permission.READ_PHONE_STATE"));
        yield return Third("com.clean.master", "3.2.8", "com.xiaomi.market", "2026-09-15 22:41:12", neverLaunched: true, label: "极速清理大师",
            permissions: P("android.permission.SYSTEM_ALERT_WINDOW", "android.permission.QUERY_ALL_PACKAGES",
                           "android.permission.RECEIVE_BOOT_COMPLETED"));
        yield return Third("com.qihoo.browser", "9.1.0", "com.android.packageinstaller", "2026-09-16 08:12:55", neverLaunched: true, label: "360 浏览器",
            permissions: P("android.permission.SYSTEM_ALERT_WINDOW", "android.permission.REQUEST_INSTALL_PACKAGES",
                           "android.permission.PACKAGE_USAGE_STATS"));
        yield return Third("com.dh.tiantian", "2.6.1", "com.xiaomi.market", "2026-09-16 08:13:04", neverLaunched: true, label: "天天赚钱",
            permissions: P("android.permission.SYSTEM_ALERT_WINDOW", "android.permission.REQUEST_INSTALL_PACKAGES",
                           "android.permission.READ_SMS", "android.permission.RECEIVE_BOOT_COMPLETED"));
        yield return Third("com.yidian.zheli", "5.0.4", "com.xiaomi.market", "2026-09-17 19:26:31", neverLaunched: true, label: "夜里小说",
            permissions: P("android.permission.SYSTEM_ALERT_WINDOW", "android.permission.REQUEST_INSTALL_PACKAGES",
                           "android.permission.POST_NOTIFICATIONS"));

        // 名单外、但使用者确实打开过的（懒人模式会处理它，但说明列看得出有人用过）
        yield return Third("com.tencent.qqlive", "8.12.5", "com.xiaomi.market", "2025-12-29 20:52:09", neverLaunched: false, label: "腾讯视频",
            permissions: P("android.permission.READ_PHONE_STATE", "android.permission.RECEIVE_BOOT_COMPLETED"));
        yield return Third("tv.danmaku.bili", "8.40.0", "com.xiaomi.market", "2025-10-11 19:05:27", neverLaunched: false, label: "哔哩哔哩",
            permissions: P("android.permission.QUERY_ALL_PACKAGES", "android.permission.POST_NOTIFICATIONS"));
    }

    private static SimulatedAdbClient.SimulatedPackage System(
        string name, string version, IReadOnlyList<string>? permissions = null) =>
        new(name, System: true, VersionName: version, FirstInstall: "2024-01-01 00:00:00",
            Permissions: permissions);

    /// <summary>没有桌面图标的系统组件——用户打不开它，才可能是纯后台的推广/遥测组件。</summary>
    private static SimulatedAdbClient.SimulatedPackage SystemLabeled(
        string name, string version, string label, bool hasLauncher = false,
        IReadOnlyList<string>? permissions = null) =>
        new(name, System: true, VersionName: version, FirstInstall: "2024-01-01 00:00:00",
            Label: label, HasLauncher: hasLauncher, Permissions: permissions);

    private static SimulatedAdbClient.SimulatedPackage Third(
        string name, string version, string installer, string firstInstall,
        bool? neverLaunched = null, string? label = null,
        IReadOnlyList<string>? permissions = null) =>
        new(name, System: false, VersionName: version, Installer: installer,
            FirstInstall: firstInstall, NeverLaunched: neverLaunched, Label: label,
            Permissions: permissions);
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
