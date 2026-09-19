# apps/optimize_bytedance.ps1
# 字节跳动系应用（抖音、飞书等）ADB 深度优化脚本
# 目标：解决抖音/飞书后台持续拉流解码、抢占 CPU 唤醒锁、后台窃取剪贴板口令、Wi-Fi/蓝牙扫描等问题

$coreHelper = Join-Path (Split-Path $PSScriptRoot -Parent) "core\adb_helper.ps1"
if (Test-Path $coreHelper) { . $coreHelper } else { $adb = "adb" }
if (-not $adb) { $adb = "adb" }

$bytedanceApps = @(
    @{ Name = "抖音";         Pkg = "com.ss.android.ugc.aweme";      Mode = "rare" },
    @{ Name = "飞书";         Pkg = "com.ss.android.lark";           Mode = "working_set" },
    @{ Name = "抖音极速版";   Pkg = "com.ss.android.ugc.aweme.lite"; Mode = "rare" },
    @{ Name = "今日头条";     Pkg = "com.ss.android.article.news";   Mode = "rare" },
    @{ Name = "剪映";         Pkg = "com.lemon.lv";                  Mode = "rare" },
    @{ Name = "番茄免费小说"; Pkg = "com.dragon.read";               Mode = "rare" },
    @{ Name = "懂车帝";       Pkg = "com.ss.android.auto";           Mode = "rare" }
)

Write-Host ">>> 正在连接设备..." -ForegroundColor Cyan
& $adb wait-for-device

$installedPkgs = & $adb shell "pm list packages --user 0"

foreach ($app in $bytedanceApps) {
    $pkg = $app.Pkg
    $name = $app.Name
    $mode = $app.Mode
    
    if (-not ($installedPkgs -match "package:$pkg`$|package:$pkg\r?`$")) {
        continue
    }

    Write-Host "`n>>> 正在优化: $name ($pkg)..." -ForegroundColor Yellow

    # 1. 限制后台硬件调用与剪贴板监听（禁止偷偷读取淘口令/抖音口令）
    & $adb shell "appops set $pkg WIFI_SCAN ignore 2>/dev/null"
    & $adb shell "appops set $pkg BLUETOOTH_SCAN ignore 2>/dev/null"
    & $adb shell "appops set $pkg READ_CLIPBOARD foreground 2>/dev/null"
    & $adb shell "appops set $pkg WRITE_CLIPBOARD foreground 2>/dev/null"
    & $adb shell "appops set $pkg MONITOR_HIGH_POWER_LOCATION ignore 2>/dev/null"
    & $adb shell "appops set $pkg MONITOR_LOCATION foreground 2>/dev/null"
    
    # 2. 彻底切断后台唤醒锁（防止后台线程如 vod_st_man、nx_main_worker 持续占用 CPU）
    & $adb shell "appops set $pkg WAKE_LOCK ignore 2>/dev/null"
    & $adb shell "appops set $pkg RUN_IN_BACKGROUND ignore 2>/dev/null"
    & $adb shell "appops set $pkg RUN_ANY_IN_BACKGROUND ignore 2>/dev/null"
    & $adb shell "appops set $pkg AUTO_START ignore 2>/dev/null"
    
    # 3. 设置待机桶策略（抖音设为 rare 极速冻结，飞书设为 working_set 保障办公通知）
    & $adb shell "am set-standby-bucket $pkg $mode 2>/dev/null"

    # 4. 执行 ART AOT 预编译（降低视频滑动与启动时的 JIT 即时编译发热）
    $compileResult = & $adb shell "cmd package compile -m speed-profile -f $pkg"
    Write-Host "  -> AOT 预编译完成: $compileResult" -ForegroundColor Green
}

Write-Host "`n=======================================================" -ForegroundColor Cyan
Write-Host ">>> 字节跳动系应用优化已全部完成！" -ForegroundColor Green
Write-Host "=======================================================" -ForegroundColor Cyan
