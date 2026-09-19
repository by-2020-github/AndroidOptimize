# ==============================================================================
# 生成文档用的界面截图（assets\）
#
# 原理：让程序以「演示模式」运行，用虚拟手机走一遍完整流程，
#       再由程序自己把界面离屏渲染成 PNG。不依赖屏幕抓取，
#       远程桌面、CI 环境里都能生成一致的截图。
#
# 用法：
#   .\tools\capture-screenshots.ps1
#   .\tools\capture-screenshots.ps1 -ExePath dist\single\AndroidOptimize-1.0.0.exe
# ==============================================================================

[CmdletBinding()]
param(
    [string]$ExePath = "",
    [string]$OutputDirectory = "assets"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if (-not $ExePath) {
    $candidates = @(
        (Join-Path $root "src\AndroidOptimize.App\bin\Release\net8.0-windows\AndroidOptimize.exe"),
        (Join-Path $root "src\AndroidOptimize.App\bin\Debug\net8.0-windows\AndroidOptimize.exe")
    )
    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ExePath -or -not (Test-Path $ExePath)) {
    throw "找不到程序，请先运行 dotnet build 或用 -ExePath 指定路径。"
}

$output = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $output | Out-Null

Write-Host "==> 使用程序：$ExePath" -ForegroundColor Cyan
Write-Host "==> 输出目录：$output" -ForegroundColor Cyan

Get-Process AndroidOptimize -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$log = Join-Path $env:TEMP "androidoptimize-shots.txt"
if (Test-Path $log) { Remove-Item $log -Force }

$process = Start-Process -FilePath $ExePath -ArgumentList @("--shots", $output) -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit(180000)) {
    $process.Kill()
    throw "截图超时（180 秒），请查看 $log。"
}

if (Test-Path $log) { Get-Content $log -Encoding UTF8 }

if ($process.ExitCode -ne 0) {
    throw "截图失败，退出码 $($process.ExitCode)。"
}

# 校验：体积过小通常说明渲染没生效（纯白 / 空白图）
$blank = @()
foreach ($file in Get-ChildItem $output -Filter "0*.png") {
    if ($file.Length -lt 20KB) { $blank += $file }
}

if ($blank.Count -eq 0) {
    Write-Host "==> 全部生成完毕" -ForegroundColor Green
    return
}

Write-Warning ("以下截图体积异常小，说明渲染没有生效：{0}" -f (($blank | ForEach-Object Name) -join "、"))
Write-Warning "原因：当前桌面会话处于「断开」或锁屏状态时，WPF 无法产生画面。"
Write-Warning "处理：重新登录桌面后再次运行本脚本即可。现在先生成占位图，避免文档出现坏链。"

# 生成占位图，明确写出「待生成」和生成方法，替换掉空白文件。
Add-Type -AssemblyName System.Drawing
$fontTitle = New-Object System.Drawing.Font("Microsoft YaHei UI", 28, [System.Drawing.FontStyle]::Bold)
$fontBody = New-Object System.Drawing.Font("Microsoft YaHei UI", 15)
$fontSmall = New-Object System.Drawing.Font("Consolas", 13)

foreach ($file in $blank) {
    $bitmap = New-Object System.Drawing.Bitmap(1360, 860)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'
    $graphics.TextRenderingHint = 'ClearTypeGridFit'
    $graphics.Clear([System.Drawing.Color]::FromArgb(245, 247, 248))

    $card = New-Object System.Drawing.Rectangle(120, 230, 1120, 400)
    $graphics.FillRectangle([System.Drawing.Brushes]::White, $card)
    $graphics.DrawRectangle((New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(207,216,220), 2)), $card)
    $graphics.DrawRectangle((New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(0,150,136), 6)), 118, 228, 1124, 404)

    $title = "此截图待生成：" + $file.Name
    $graphics.DrawString($title, $fontTitle, [System.Drawing.Brushes]::DimGray, 170, 290)
    $graphics.DrawString("原因：生成截图时，当前 Windows 桌面会话处于「断开」或锁屏状态，WPF 渲染不到画面。", $fontBody, [System.Drawing.Brushes]::Gray, 172, 360)
    $graphics.DrawString("处理：重新登录桌面后，在项目根目录执行下面这一条命令即可自动替换本图：", $fontBody, [System.Drawing.Brushes]::Gray, 172, 400)
    $graphics.DrawString(".\tools\capture-screenshots.ps1", $fontSmall, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0,105,92))), 172, 448)
    $graphics.DrawString("截图由程序自己的「演示模式 + 离屏渲染」生成，不依赖屏幕抓取，内容与真实界面一致。", $fontBody, [System.Drawing.Brushes]::Gray, 172, 530)

    $graphics.Dispose()
    $bitmap.Save($file.FullName, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host ("  已生成占位图 {0}" -f $file.Name)
}
