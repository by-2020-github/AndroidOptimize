# tools/analyze_heat.ps1
# 实时 CPU 占用与发热异常分析探针

$coreHelper = Join-Path $PSScriptRoot "core\adb_helper.ps1"
if (Test-Path $coreHelper) { . $coreHelper } else { $adb = "adb" }
if (-not $adb) { $adb = "adb" }

$reportsDir = Join-Path $PSScriptRoot "reports"
if (-not (Test-Path $reportsDir)) { New-Item -ItemType Directory -Path $reportsDir -Force | Out-Null }
$outputReport = Join-Path $reportsDir "heat_analysis_report.md"

$duration = 180 # 3 minutes
$interval = 1   # sample interval
$iterations = [int]($duration / $interval)

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "🚨 性能与发热分析追踪器已启动" -ForegroundColor Green
Write-Host ">> 将连续采样 3 分钟，每 1 秒记录一次进程及负荷状态。" -ForegroundColor Yellow
Write-Host ">> 请在此期间让手机保持在您之前觉得发热的状态，正常使用或放置不动均可。" -ForegroundColor Yellow
Write-Host "==========================================" -ForegroundColor Cyan

$stats = @{}

for ($i = 0; $i -lt $iterations; $i++) {
    $progress = [math]::Round((($i + 1) / $iterations) * 100)
    Write-Progress -Activity "监控探针运行中 (请勿关闭终端)" -Status "分析进度: $progress% | $($iterations - $i) 个采样点剩余" -PercentComplete $progress
    
    # 抓取 top 排行榜
    $top = & $adb shell top -b -n 1 -m 20 2>$null
    foreach ($line in $top) {
        $parts = $line.Trim() -split '\s+'
        # 标准输出为: PID USER PR NI VIRT RES SHR S %CPU %MEM TIME+ ARGS
        if ($parts.Count -ge 12 -and $parts[0] -match '^\d+$') {
            $process = $parts[11..($parts.Count - 1)] -join " "
            $cpuStr = $parts[8]
            $cpu = 0
            if ([double]::TryParse($cpuStr, [ref]$cpu)) {
                if (-not $stats.ContainsKey($process)) {
                    $stats[$process] = @{ Samples=0; Total=0; Max=0 }
                }
                $stats[$process].Samples++
                $stats[$process].Total += $cpu
                if ($cpu -gt $stats[$process].Max) { $stats[$process].Max = $cpu }
            }
        }
    }
    Start-Sleep -Seconds $interval
}

Write-Progress -Activity "监控探针运行中" -Completed
Write-Host "`n✅ 设备计算资源采样完毕，正在进行数据聚合..." -ForegroundColor Green

$markdown = @"
# 🌡️ 3分钟实时发热与性能异常审查报告

> **生成时间**：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
> **测试时长**：3 分钟
> **分析逻辑**：通过在 3 分钟内发起多次 CPU 芯片切片探测，筛选出这期间高频唤醒、运算死循环或霸占计算资源的“毒瘤”应用。通常 CPU 平均占用率 > **10%** 且长时间不休眠的即为发热元凶。

## ⚠️ 高嫌疑计算资源消耗榜单 (重点研判对象)
| 进程/组件名 (Process) | 3分钟内平均CPU | 检测峰值CPU | 抓取活跃频次 | 诊断与处理建议 |
|---|---|---|---|---|
"@

$issuesFound = $false
$results = @()

foreach ($key in $stats.Keys) {
    $avg = [math]::Round($stats[$key].Total / $iterations, 2)
    $max = $stats[$key].Max
    $samp = $stats[$key].Samples
    
    # 仅过滤出均值 > 3% 或峰值 > 15% 的进程，避免报表太长
    if ($avg -gt 3 -or $max -gt 15) {
        $results += [PSCustomObject]@{
            Process = $key
            Avg = $avg
            Max = $max
            Samp = $samp
        }
    }
}

$results = $results | Sort-Object Avg -Descending

foreach ($r in $results) {
    $issuesFound = $true
    $adv = "正常系统环境响应/无害波动"
    
    if ($r.Avg -gt 30) { $adv = "🔥 **严重发热核心**：持续大量霸占算力，建议立刻结束运行并限制后台" }
    elseif ($r.Max -gt 40 -and $r.Samp -gt 5) { $adv = "⚠️ **频发高负载**：代码可能存在暴力唤醒或重绘异常，容易累积发热" }
    elseif ($r.Process -match "com.miui.home") { $adv = "桌面管理器 (若占用极高说明桌面组件卡死，建议重启手机)" }
    elseif ($r.Process -match "surface|system_server|hardware") { $adv = "安卓底层绘制组件 (通常是替被点亮的前台应用处理画面)" }
    elseif ($r.Process -match "tencent|mm|qq") { $adv = "通讯应用高强度轮询与网络保活，重灾区" }
    elseif ($r.Process -match "webview|chrome|browser") { $adv = "渲染网页元素通常消耗较高" }

    $markdown += "`n| \`$($r.Process)\` | **$($r.Avg)%** | $($r.Max)% | $($r.Samp)/$iterations | $adv |"
}

if (-not $issuesFound) {
    $markdown += "`n| 未检测到发热级异常负载 | - | - | - | 您的手机目前处于正常的息屏/低压工作负载内，如果依然发烫可能为充电或外围硬件引起。 |"
}

$markdown += "`n`n---`n*提示：排在前三的高耗电应用，建议划脱多任务强行结束，或在电池中心开启【后台智能限制】*"

Set-Content $outputReport -Value $markdown -Encoding UTF8
Write-Host "✅ 测试诊断完毕。已生成审查报告: $outputReport，请打开该文件查看分析结论！" -ForegroundColor Cyan
