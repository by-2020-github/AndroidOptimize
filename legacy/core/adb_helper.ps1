# core/adb_helper.ps1
# ADB 统一环境检测与辅助加载模块（优先使用内置 bin/adb.exe）

function Get-AdbExecutable {
    [CmdletBinding()]
    param()

    # 1. 优先使用仓库内置的 adb（新布局 vendor\platform-tools，旧布局 bin）
    #    注意：本脚本位于 legacy\core\，仓库根目录要向上两级。
    $repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $builtinCandidates = @(
        (Join-Path $repoRoot "vendor\platform-tools\adb.exe"),
        (Join-Path $repoRoot "bin\adb.exe")
    )
    $builtinAdb = $builtinCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($builtinAdb) {
        $binDir = Split-Path $builtinAdb
        if ($env:PATH -notlike "*$binDir*") {
            $env:PATH = "$binDir;" + $env:PATH
        }
        return $builtinAdb
    }

    # 2. 检查当前环境变量 PATH 中是否已存在 adb
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    # 3. 检查常见 Android SDK / platform-tools 安装路径
    $candidatePaths = @(
        "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
        "$env:ProgramFiles\Android\platform-tools\adb.exe",
        "${env:ProgramFiles(x86)}\Android\platform-tools\adb.exe",
        "C:\platform-tools\adb.exe",
        "D:\platform-tools\adb.exe"
    )

    foreach ($path in $candidatePaths) {
        if (Test-Path $path) {
            $platformToolsDir = Split-Path $path
            if ($env:PATH -notlike "*$platformToolsDir*") {
                $env:PATH += ";$platformToolsDir"
            }
            return $path
        }
    }

    Write-Error "【错误】未找到 adb.exe，请确保内置 bin 目录或系统已配置 ADB。"
    return $null
}

$global:AdbPath = Get-AdbExecutable
$global:adb = $global:AdbPath
