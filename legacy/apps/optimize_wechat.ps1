# apps/optimize_wechat.ps1
# 微信专项 ADB 深度优化脚本
# 目标：限制微信后台滥用硬件、禁止隐式唤醒、减少后台常驻耗电与发热，同时保留消息推送与语音通话基本功能

$coreHelper = Join-Path (Split-Path $PSScriptRoot -Parent) "core\adb_helper.ps1"
if (Test-Path $coreHelper) { . $coreHelper } else { $adb = "adb" }
if (-not $adb) { $adb = "adb" }

$pkg = "com.tencent.mm"

Write-Host ">>> 正在连接设备..." -ForegroundColor Cyan
& $adb wait-for-device

Write-Host "`n>>> [1/5] 执行 ART AOT 全局预编译 (减少冷启动卡顿与后台 JIT 编译发热)..." -ForegroundColor Yellow
& $adb shell "cmd package compile -m speed-profile -f $pkg"

Write-Host "`n>>> [2/5] 限制微信后台滥用硬件资源 (仅允许前台使用)..." -ForegroundColor Yellow
# 禁止后台扫描 Wi-Fi（减少后台定位扫描与 CPU 唤醒）
& $adb shell "appops set $pkg WIFI_SCAN ignore"
# 禁止后台读取剪贴板（保护隐私并减少跨应用剪贴板监听）
& $adb shell "appops set $pkg READ_CLIPBOARD foreground"
# 禁止后台高耗电定位监控
& $adb shell "appops set $pkg MONITOR_HIGH_POWER_LOCATION ignore"
& $adb shell "appops set $pkg MONITOR_LOCATION foreground"
# 禁止后台调用蓝牙扫描
& $adb shell "appops set $pkg BLUETOOTH_SCAN ignore"

Write-Host "`n>>> [3/5] 限制微信后台过度唤醒 (WAKE_LOCK)..." -ForegroundColor Yellow
# 注意：如果发现锁屏收不到语音通话或消息严重延迟，可改回 allow
& $adb shell "appops set $pkg WAKE_LOCK ignore"

Write-Host "`n>>> [4/5] 优化应用待机与休眠桶策略 (App Standby Bucket)..." -ForegroundColor Yellow
# 将微信待机状态设为 WORKING_SET（活跃但受限，防止无节制后台耗电）
& $adb shell "am set-standby-bucket $pkg working_set"

Write-Host "`n>>> [5/5] 优化应用后台冻结策略..." -ForegroundColor Yellow
& $adb shell "appops set $pkg RUN_IN_BACKGROUND allow"

Write-Host "`n=======================================================" -ForegroundColor Cyan
Write-Host ">>> 微信专项优化已全部应用！" -ForegroundColor Green
Write-Host "=======================================================" -ForegroundColor Cyan
