# devices/xiaomi/restore_guardprovider.ps1
# 恢复小米安全守护/GuardProvider组件脚本

$coreHelper = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "core\adb_helper.ps1"
if (Test-Path $coreHelper) { . $coreHelper } else { $env:PATH += ";$env:LOCALAPPDATA\Android\Sdk\platform-tools" }

$package = "com.miui.guardprovider"

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

Write-Host "开始恢复 $package ..." -ForegroundColor Cyan

$installExisting = Invoke-Adb @("shell", "cmd", "package", "install-existing", "--user", "0", $package)
$enable = Invoke-Adb @("shell", "pm", "enable", "--user", "0", $package)

if ($installExisting.ExitCode -eq 0) {
    @($installExisting.Output) | ForEach-Object {
        if ($_ -and $_.ToString().Trim()) {
            Write-Host $_
        }
    }
}

if ($enable.ExitCode -eq 0 -and ($enable.Output -join "`n") -notmatch "Failure") {
    Write-Host "已恢复并启用 $package" -ForegroundColor Green
} else {
    Write-Host "恢复命令已执行，请检查下方输出。" -ForegroundColor Yellow
    @($enable.Output) | ForEach-Object {
        if ($_ -and $_.ToString().Trim()) {
            Write-Host $_
        }
    }
}
