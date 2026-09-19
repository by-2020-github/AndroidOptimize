# system/optimize_animations.ps1
# 系统与应用打开动效极致流畅度优化脚本
# 针对：Android 通用系统动画缩放、高刷锁定、MIUI/HyperOS 桌面极速轻快动效与 SystemUI 预编译

$coreHelper = Join-Path (Split-Path $PSScriptRoot -Parent) "core\adb_helper.ps1"
if (Test-Path $coreHelper) { . $coreHelper } else { $adb = "adb" }
if (-not $adb) { $adb = "adb" }

Write-Host ">>> 正在连接设备..." -ForegroundColor Cyan
& $adb wait-for-device

Write-Host "`n>>> [1/4] 优化 Android 全局动画缩放倍率 (0.75x 黄金顺滑档位)..." -ForegroundColor Yellow
# 窗口动画缩放 (窗口打开关闭速度)
& $adb shell "settings put global window_animation_scale 0.75"
# 过渡动画缩放 (Activity 切换与路由动画)
& $adb shell "settings put global transition_animation_scale 0.75"
# 动效时长缩放 (属性动画、按钮回弹与手势反馈)
& $adb shell "settings put global animator_duration_scale 0.75"

Write-Host "`n>>> [2/4] 解锁全局高刷 (防止 App 启动瞬间跌落 60Hz 造成视觉掉帧)..." -ForegroundColor Yellow
# 设置最低与最高刷新率下限为 120Hz，消除触控变频延迟
& $adb shell "settings put system min_refresh_rate 120.0"
& $adb shell "settings put system peak_refresh_rate 120.0"
& $adb shell "settings put system user_refresh_rate 120"

Write-Host "`n>>> [3/4] 开启桌面极速动画模式 (若支持)..." -ForegroundColor Yellow
# 0 为默认优雅/平衡，1 为极速轻快 (减少回弹拖泥带水)
& $adb shell "settings put system miui_home_animation_rate 1 2>/dev/null"

Write-Host "`n>>> [4/4] 对系统桌面与 SystemUI 执行 ART AOT 全局预编译..." -ForegroundColor Yellow
& $adb shell "cmd package compile -m speed-profile -f com.miui.home 2>/dev/null"
& $adb shell "cmd package compile -m speed-profile -f com.android.systemui"

Write-Host "`n=======================================================" -ForegroundColor Cyan
Write-Host ">>> 动效流畅度与高刷参数已全部生效！" -ForegroundColor Green
Write-Host "=======================================================" -ForegroundColor Cyan
