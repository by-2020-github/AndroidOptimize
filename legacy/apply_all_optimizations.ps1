# ==============================================================================
# Android 全机型优化主入口脚本 (AndroidOptimize)
# 支持：自动探测设备厂商 (Xiaomi/Redmi、vivo/iQOO、OPPO/OnePlus 等) 并执行对应优化
# ==============================================================================

$ErrorActionPreference = "Continue"

# 1. 引入内置 ADB 驱动辅助
. "$PSScriptRoot\core\adb_helper.ps1"
if (-not $global:AdbPath) {
    Write-Host "【错误】无法定位 ADB 工具，请检查 bin\ 目录是否完整。" -ForegroundColor Red
    exit 1
}

$adb = $global:AdbPath

Write-Host "=======================================================" -ForegroundColor Cyan
Write-Host "      🚀 Android 全机型 ADB 极速优化工具箱" -ForegroundColor Green
Write-Host "=======================================================" -ForegroundColor Cyan

Write-Host "`n>>> [0/4] 正在等待设备连接与 USB 调试授权..." -ForegroundColor Yellow
& $adb wait-for-device

# 获取设备基础信息
$brand = (& $adb shell getprop ro.product.brand 2>$null).Trim().ToLower()
$manufacturer = (& $adb shell getprop ro.product.manufacturer 2>$null).Trim().ToLower()
$model = (& $adb shell getprop ro.product.model 2>$null).Trim()

Write-Host ">>> 检测到设备型号: $model | 品牌: $brand / $manufacturer" -ForegroundColor Green

# 2. 机型专属系统精简与优化
Write-Host "`n>>> [1/4] 执行品牌专属系统组件精简..." -ForegroundColor Yellow
if ($brand -match "xiaomi|redmi|poco|blackshark" -or $manufacturer -match "xiaomi") {
    Write-Host "  -> 匹配到 小米 (HyperOS / MIUI) 策略" -ForegroundColor Cyan
    & "$PSScriptRoot\devices\xiaomi\optimize_miui.ps1"
} elseif ($brand -match "vivo|iqoo" -or $manufacturer -match "vivo") {
    Write-Host "  -> 匹配到 vivo (OriginOS) 策略" -ForegroundColor Cyan
    if (Test-Path "$PSScriptRoot\devices\vivo\optimize_origin_os.ps1") {
        & "$PSScriptRoot\devices\vivo\optimize_origin_os.ps1"
    } else {
        Write-Host "  [提示] vivo 专属优化模块持续开发中，跳过厂商精简..." -ForegroundColor DarkYellow
    }
} elseif ($brand -match "oppo|oneplus|realme" -or $manufacturer -match "oppo|oneplus") {
    Write-Host "  -> 匹配到 OPPO / 一加 / 真我 (ColorOS) 策略" -ForegroundColor Cyan
    if (Test-Path "$PSScriptRoot\devices\oppo\optimize_color_os.ps1") {
        & "$PSScriptRoot\devices\oppo\optimize_color_os.ps1"
    } else {
        Write-Host "  [提示] OPPO 专属优化模块持续开发中，跳过厂商精简..." -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  [提示] 未识别到特定厂商定制模块，跳过系统私有组件精简..." -ForegroundColor DarkGray
}

# 3. 通用系统动效与高刷策略
Write-Host "`n>>> [2/4] 应用全局动效与高刷流畅度配置..." -ForegroundColor Yellow
& "$PSScriptRoot\system\optimize_animations.ps1"

# 4. 重点通用应用优化 (微信、电商、生活服务)
Write-Host "`n>>> [3/4] 应用微信、阿里、京东、美团、拼多多深度后台限制..." -ForegroundColor Yellow
& "$PSScriptRoot\apps\optimize_wechat.ps1"
& "$PSScriptRoot\apps\optimize_commercial_apps.ps1"

# 5. 字节跳动系应用优化 (抖音、飞书等)
Write-Host "`n>>> [4/4] 应用字节跳动系（抖音/飞书/剪映）后台限制与预编译..." -ForegroundColor Yellow
& "$PSScriptRoot\apps\optimize_bytedance.ps1"

Write-Host "`n=======================================================" -ForegroundColor Cyan
Write-Host "🎉 全部优化策略已成功执行完毕！建议重启手机以获得最佳体验。" -ForegroundColor Green
Write-Host "=======================================================" -ForegroundColor Cyan
