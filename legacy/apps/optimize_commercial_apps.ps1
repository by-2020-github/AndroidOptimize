# apps/optimize_commercial_apps.ps1
# 电商与生活服务全家桶（阿里/京东/美团等）ADB 深度优化脚本
# 目标：彻底禁止后台自启与唤醒锁、关闭后台剪贴板窃取、禁止后台 Wi-Fi/高功耗定位扫描、设置稀疏待机桶(rare)、AOT预编译

$coreHelper = Join-Path (Split-Path $PSScriptRoot -Parent) "core\adb_helper.ps1"
if (Test-Path $coreHelper) { . $coreHelper } else { $adb = "adb" }
if (-not $adb) { $adb = "adb" }

$targetApps = @(
    # --- 阿里系 ---
    @{ Name = "手机淘宝";   Pkg = "com.taobao.taobao" },
    @{ Name = "闲鱼";       Pkg = "com.taobao.idlefish" },
    @{ Name = "飞猪旅行";   Pkg = "com.taobao.trip" },
    @{ Name = "钉钉";       Pkg = "com.alibaba.android.rimet" },
    @{ Name = "高德地图";   Pkg = "com.autonavi.minimap" },
    @{ Name = "阿里云";     Pkg = "com.alibaba.aliyun" },
    @{ Name = "天猫精灵";   Pkg = "com.alibaba.ailabs.tg" },
    
    # --- 京东系 ---
    @{ Name = "京东";       Pkg = "com.jingdong.app.mall" },
    @{ Name = "京东金融";   Pkg = "com.jd.jrapp" },
    
    # --- 美团系 ---
    @{ Name = "美团";       Pkg = "com.sankuai.meituan" },
    @{ Name = "美团外卖";   Pkg = "com.sankuai.meituan.takeoutnew" },

    # --- 拼多多 ---
    @{ Name = "拼多多";     Pkg = "com.xunmeng.pinduoduo" }
)

Write-Host ">>> 正在连接设备..." -ForegroundColor Cyan
& $adb wait-for-device

# 获取当前已安装的所有包
$installedPkgs = & $adb shell "pm list packages --user 0"

foreach ($app in $targetApps) {
    $pkg = $app.Pkg
    $name = $app.Name
    
    if (-not ($installedPkgs -match "package:$pkg")) {
        Write-Host "[跳过] $name ($pkg) 未在当前用户下安装" -ForegroundColor DarkGray
        continue
    }

    Write-Host "`n>>> 正在优化: $name ($pkg)..." -ForegroundColor Yellow

    # 1. 限制后台硬件访问与剪贴板监听
    & $adb shell "appops set $pkg WIFI_SCAN ignore 2>/dev/null"
    & $adb shell "appops set $pkg BLUETOOTH_SCAN ignore 2>/dev/null"
    & $adb shell "appops set $pkg READ_CLIPBOARD foreground 2>/dev/null"
    & $adb shell "appops set $pkg WRITE_CLIPBOARD foreground 2>/dev/null"
    & $adb shell "appops set $pkg MONITOR_HIGH_POWER_LOCATION ignore 2>/dev/null"
    & $adb shell "appops set $pkg MONITOR_LOCATION foreground 2>/dev/null"

    # 2. 封杀后台唤醒锁与后台运行权限
    & $adb shell "appops set $pkg WAKE_LOCK ignore 2>/dev/null"
    & $adb shell "appops set $pkg RUN_IN_BACKGROUND ignore 2>/dev/null"
    & $adb shell "appops set $pkg RUN_ANY_IN_BACKGROUND ignore 2>/dev/null"
    & $adb shell "appops set $pkg AUTO_START ignore 2>/dev/null"

    # 3. 待机分桶调优 (钉钉等办公类设为 working_set，纯电商设为 rare 极低频休眠)
    if ($pkg -eq "com.alibaba.android.rimet") {
        & $adb shell "am set-standby-bucket $pkg working_set 2>/dev/null"
    } else {
        & $adb shell "am set-standby-bucket $pkg rare 2>/dev/null"
    }

    # 4. 执行 AOT 预编译优化
    $compileResult = & $adb shell "cmd package compile -m speed-profile -f $pkg"
    Write-Host "  -> AOT 预编译完成: $compileResult" -ForegroundColor Green
}

Write-Host "`n=======================================================" -ForegroundColor Cyan
Write-Host ">>> 电商与生活服务类应用优化已全部完成！" -ForegroundColor Green
Write-Host "=======================================================" -ForegroundColor Cyan
