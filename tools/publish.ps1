# ==============================================================================
# AndroidOptimize 桌面版打包脚本
#
# 用法：
#   .\publish.ps1                    单个 exe（推荐，直接传到 GitHub/Gitee Release）
#                                    用户下载后双击即可，无需安装 .NET，无需任何其它文件
#   .\publish.ps1 -Folder            文件夹形式，额外带上 data\ 名单与 bin\ adb
#                                    方便手工改名单，或做绿色版
#   .\publish.ps1 -FrameworkDependent
#                                    依赖 .NET 8 桌面运行时，体积最小
#   .\publish.ps1 -SkipBuild         只重新组装目录，不重新编译
#   .\publish.ps1 -OutputDirectory release
#                                    输出到 release\（这个目录是要提交进仓库的）
#
# 参数：
#   -Version <字符串>   在文件名里带上版本号，例如 AndroidOptimize-0.1.0.exe
# ==============================================================================

[CmdletBinding()]
param(
    [switch]$Folder,
    [switch]$FrameworkDependent,
    [switch]$SkipBuild,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
# 本脚本位于 tools\ 下，仓库根目录是它的上一级
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\AndroidOptimize.App\AndroidOptimize.App.csproj"

$singleFile = -not $Folder
$defaultOutput = if ($singleFile) { "dist\single" } else { "dist\AndroidOptimize" }
$output = Join-Path $root $(if ($OutputDirectory) { $OutputDirectory } else { $defaultOutput })

# 只清理本脚本自己的输出目录，且必须位于仓库的 dist\ 或 release\ 下。
$allowedRoots = @(
    [System.IO.Path]::GetFullPath((Join-Path $root "dist")),
    [System.IO.Path]::GetFullPath((Join-Path $root "release"))
)
$resolvedOutput = [System.IO.Path]::GetFullPath($output)
if (Test-Path $resolvedOutput) {
    $allowed = $false
    foreach ($allowedRoot in $allowedRoots) {
        if ($resolvedOutput.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $allowed = $true
            break
        }
    }
    if (-not $allowed) {
        throw "拒绝清理目录 $resolvedOutput：它不在仓库的 dist\ 或 release\ 目录下。"
    }

    # 保留目录里的 README.md（对浏览仓库的人是说明），其余产物清掉重来。
    Get-ChildItem -LiteralPath $resolvedOutput -Force |
        Where-Object { $_.Name -ne "README.md" } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

Write-Host "==> 输出目录：$resolvedOutput" -ForegroundColor Cyan

if (-not $SkipBuild) {
    Write-Host "==> 编译并发布…" -ForegroundColor Cyan
    $publishArgs = @(
        "publish", $project,
        "-c", $Configuration,
        "-r", $Runtime,
        "-o", $resolvedOutput,
        "--nologo",
        "-p:DebugType=none",
        "-p:GenerateDocumentationFile=false"
    )

    if ($FrameworkDependent) {
        $publishArgs += "--self-contained=false"
    } else {
        $publishArgs += "--self-contained=true"
    }

    if ($singleFile) {
        # 单文件：把托管代码、WPF 资源与原生库全部打进一个 exe，并启用压缩。
        $publishArgs += "-p:PublishSingleFile=true"
        $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
        $publishArgs += "-p:EnableCompressionInSingleFile=true"
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败（退出码 $LASTEXITCODE）。"
    }
}

$exe = Join-Path $resolvedOutput "AndroidOptimize.exe"
if (-not (Test-Path $exe)) {
    throw "发布目录里没有找到 AndroidOptimize.exe。"
}

if (-not $singleFile) {
    # ---- 文件夹形式：额外带上 adb 与名单，方便手工维护 ----
    Write-Host "==> 复制内置 ADB 与名单数据…" -ForegroundColor Cyan

    # 只带运行必需的三个文件与许可声明；vendor 里的 fastboot / sqlite3 等本程序用不到。
    $adbSource = Join-Path $root "vendor\platform-tools"
    $adbTarget = Join-Path $resolvedOutput "bin"
    New-Item -ItemType Directory -Force -Path $adbTarget | Out-Null

    foreach ($name in @("adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll", "NOTICE.txt", "source.properties")) {
        $source = Join-Path $adbSource $name
        if (Test-Path $source) {
            Copy-Item -Path $source -Destination $adbTarget -Force
        } else {
            Write-Warning "缺少 $name，发布包里可能无法连接手机。"
        }
    }

    $dataTarget = Join-Path $resolvedOutput "data"
    New-Item -ItemType Directory -Force -Path $dataTarget | Out-Null
    Copy-Item -Path (Join-Path $root "data\*.json") -Destination $dataTarget -Force

    Copy-Item -Path (Join-Path $root "README.md") -Destination $resolvedOutput -Force -ErrorAction SilentlyContinue
}

# ---- 自检：确保发出去的包一定能跑 ----
Write-Host "==> 运行打包后自检…" -ForegroundColor Cyan
$selfTestOutput = Join-Path $env:TEMP "androidoptimize-publish-selftest.txt"
if (Test-Path $selfTestOutput) { Remove-Item $selfTestOutput -Force }
Start-Process -FilePath $exe -ArgumentList @("--selftest", $selfTestOutput) -Wait -WindowStyle Hidden

if (-not (Test-Path $selfTestOutput)) {
    throw "自检没有产生输出，发布包可能不可用。"
}

$selfTestText = Get-Content $selfTestOutput -Raw -Encoding UTF8
Remove-Item $selfTestOutput -Force -ErrorAction SilentlyContinue

($selfTestText -split "`r?`n" | Where-Object { $_ -match "检查项|全部通过|失败项" }) | ForEach-Object { Write-Host "    $_" }

if ($selfTestText -notmatch "全部通过") {
    throw "打包自检未全部通过，请先修复再发布。"
}

# ---- 单文件模式：清掉散落的附属文件，只留 exe ----
if ($singleFile) {
    # dotnet publish 会把 data\ 等 Content 一并拷过来；单文件交付只需要那一个 exe，
    # 名单与 adb 都已内嵌在程序里（要手工改名单时用 -Folder 模式）。
    Get-ChildItem -Path $resolvedOutput |
        Where-Object { $_.Name -ne "AndroidOptimize.exe" -and $_.Name -ne "README.md" } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }

    if ($Version) {
        $finalExe = Join-Path $resolvedOutput "AndroidOptimize-$Version.exe"
        Move-Item -LiteralPath $exe -Destination $finalExe -Force
        $exe = $finalExe
    }
}

$size = (Get-ChildItem $resolvedOutput -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("==> 完成：{0}" -f $exe) -ForegroundColor Green
Write-Host ("    大小 {0:N1} MB。把这个文件上传到 Release，用户下载后双击就能用。" -f $size) -ForegroundColor Green
