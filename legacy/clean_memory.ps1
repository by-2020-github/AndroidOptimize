# tools/clean_memory.ps1
# 一键深度清理后台内存与僵尸进程脚本

$coreHelper = Join-Path $PSScriptRoot "core\adb_helper.ps1"
if (Test-Path $coreHelper) { . $coreHelper } else { $adb = "adb" }
if (-not $adb) { $adb = "adb" }

Write-Host ">>> 正在连接设备..." -ForegroundColor Cyan
& $adb wait-for-device

Write-Host "`n>>> [1/3] 正在安全清理所有非活跃后台进程 (am kill-all)..." -ForegroundColor Yellow
& $adb shell "am kill-all"

Write-Host "`n>>> [2/3] 正在强行终止常见大型吃内存驻留应用 (短视频、游戏、电商等)..." -ForegroundColor Yellow
$heavyApps = @(
    "com.ss.android.ugc.aweme",        # 抖音
    "com.ss.android.ugc.aweme.lite",   # 抖音极速版
    "com.tencent.tmgp.sgame",          # 王者荣耀
    "com.tencent.tmgp.pubgmhd",        # 和平精英
    "com.miHoYo.Yuanshen",             # 原神
    "com.taobao.taobao",               # 淘宝
    "com.jingdong.app.mall",           # 京东
    "com.xunmeng.pinduoduo",           # 拼多多
    "com.bilibili.app.in",             # 哔哩哔哩
    "tv.danmaku.bili",                 # 哔哩哔哩
    "com.kuaishou.nebula",             # 快手
    "com.smile.gifmaker"               # 快手
)

foreach ($app in $heavyApps) {
    & $adb shell "am force-stop $app 2>/dev/null"
}

Write-Host "`n>>> [3/3] 正在触发系统级内存整理与缓存回收 (Trim Caches)..." -ForegroundColor Yellow
& $adb shell "cmd activity trim-memory com.android.systemui COMPLETE 2>/dev/null"

Write-Host "`n=======================================================" -ForegroundColor Cyan
Write-Host ">>> 内存清理完成，前台可用 RAM 已最大化释放！" -ForegroundColor Green
Write-Host "=======================================================" -ForegroundColor Cyan
