# devices/xiaomi/optimize_miui.ps1
# 小米 (HyperOS / MIUI) 系统组件与预装广告精简脚本

$coreHelper = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "core\adb_helper.ps1"
if (Test-Path $coreHelper) { . $coreHelper } else { $env:PATH += ";$env:LOCALAPPDATA\Android\Sdk\platform-tools" }

$packages = @(
    "com.android.updater",
    "com.miui.systemAdSolution",
    "com.miui.analytics",
    "com.miui.uireporter",
    "com.miui.bugreport",
    "com.bsp.catchlog",
    "com.debug.loggerui",
    "com.miui.voiceassist",
    "com.miui.voiceassistProxy",
    "com.xiaomi.aiasst.service",
    "com.xiaomi.aicr",
    "com.xiaomi.aireco",
    "com.miui.personalassistant",
    "com.miui.hybrid",
    "com.miui.yellowpage",
    "com.android.browser",
    "com.miui.video",
    "com.baidu.BaiduMap",
    "com.xiaomi.market",
    "com.xiaomi.minigame",
    "com.xiaomi.migameservice",
    "com.xiaomi.gamecenter.sdk.service",
    "com.xiaomi.joyose",
    "com.miui.mishare.connectivity",
    "com.xiaomi.mi_connect_service",
    "com.milink.service",
    "com.miui.guardprovider",
    "com.miui.accessibility",
    "com.xiaomi.mis",
    "com.mfashiongallery.emag",
    "com.miui.cleanmaster",
    "com.android.quicksearchbox",
    "com.miui.contentextension",
    "com.miui.contentcatcher",
    "com.xiaomi.macro",
    "com.miui.thirdappassistant",
    "com.xiaomi.barrage",
    "com.miui.compass",
    "com.xiaomi.scanner",
    "com.miui.cloudservice",
    "com.miui.micloudsync",
    "com.miui.cloudbackup",
    "com.xiaomi.micloud.sdk",
    "com.miui.voicetrigger",
    "com.xiaomi.aiasst.vision",
    "com.miui.voiceassistoverlay"
)

function Invoke-Adb {
    param(
        [string[]]$Arguments
    )

    $output = & adb @Arguments 2>&1
    return [PSCustomObject]@{
        ExitCode = $LASTEXITCODE
        Output = @($output)
    }
}

function Test-InstalledForUser {
    param(
        [string]$PackageName
    )

    $result = Invoke-Adb @("shell", "dumpsys", "package", $PackageName)
    if ($result.ExitCode -ne 0) {
        throw "无法读取包状态：$PackageName"
    }

    $userState = $result.Output | Select-String -Pattern 'User 0:.*installed=(true|false)' | Select-Object -First 1
    if (-not $userState) {
        return $false
    }

    return $userState.Matches[0].Groups[1].Value -eq 'true'
}

Write-Host "开始执行 MIUI / HyperOS 精简..." -ForegroundColor Cyan

foreach ($package in $packages) {
    try {
        if (-not (Test-InstalledForUser -PackageName $package)) {
            Write-Host "[跳过] $package 已不在用户 0 中" -ForegroundColor DarkGray
            continue
        }

        $uninstall = Invoke-Adb @("shell", "pm", "uninstall", "-k", "--user", "0", $package)
        if ($uninstall.ExitCode -eq 0 -and ($uninstall.Output -join "`n") -match "Success") {
            Write-Host "[已卸载] $package" -ForegroundColor Green
            continue
        }

        $disable = Invoke-Adb @("shell", "pm", "disable-user", "--user", "0", $package)
        if ($disable.ExitCode -eq 0 -and ($disable.Output -join "`n") -notmatch "Failure") {
            Write-Host "[已禁用] $package" -ForegroundColor Yellow
            continue
        }

        Write-Host "[失败] $package" -ForegroundColor Red
        @($uninstall.Output + $disable.Output) | ForEach-Object {
            if ($_ -and $_.ToString().Trim()) {
                Write-Host "  $_"
            }
        }
    }
    catch {
        Write-Host "[异常] $package $_" -ForegroundColor Red
    }
}

Write-Host "执行完成。建议随后运行 .\generate_md.ps1 刷新手册。" -ForegroundColor Cyan
