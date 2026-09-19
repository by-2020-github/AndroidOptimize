# devices/xiaomi/generate_md.ps1
# 小米 (HyperOS / MIUI) 精简优化手册动态生成脚本

$coreHelper = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "core\adb_helper.ps1"
if (Test-Path $coreHelper) { . $coreHelper } else { $env:PATH += ";$env:LOCALAPPDATA\Android\Sdk\platform-tools" }

$outputFile = Join-Path $PSScriptRoot "docs\小米系统精简优化手册.md"
$packageProfiles = @{
    "com.android.updater" = @{ Category = "系统更新"; Risk = "低"; Action = "建议精简"; Reason = "禁用 OTA 系统更新，避免后台自动下载更新包或恢复预装组件" }
    "com.miui.systemAdSolution" = @{ Category = "广告与跟踪"; Risk = "低"; Action = "建议精简"; Reason = "MSA 系统级广告服务，常用于系统广告投放" }
    "com.miui.analytics" = @{ Category = "广告与跟踪"; Risk = "低"; Action = "建议精简"; Reason = "数据分析与行为追踪组件" }
    "com.miui.uireporter" = @{ Category = "广告与跟踪"; Risk = "低"; Action = "建议精简"; Reason = "UI 行为上报与统计" }
    "com.miui.bugreport" = @{ Category = "广告与跟踪"; Risk = "低"; Action = "建议精简"; Reason = "Bug 反馈与日志采集" }
    "com.bsp.catchlog" = @{ Category = "广告与跟踪"; Risk = "中"; Action = "按需精简"; Reason = "底层日志收集组件，影响问题诊断能力" }
    "com.debug.loggerui" = @{ Category = "广告与跟踪"; Risk = "低"; Action = "建议精简"; Reason = "调试日志界面" }
    "com.miui.voiceassist" = @{ Category = "小爱与语音"; Risk = "低"; Action = "按需精简"; Reason = "小爱语音主服务，不使用语音助手时可移除" }
    "com.miui.voiceassistProxy" = @{ Category = "小爱与语音"; Risk = "低"; Action = "按需精简"; Reason = "语音助手代理服务" }
    "com.xiaomi.aiasst.service" = @{ Category = "小爱与语音"; Risk = "低"; Action = "按需精简"; Reason = "AI 助手服务" }
    "com.xiaomi.aicr" = @{ Category = "小爱与语音"; Risk = "低"; Action = "按需精简"; Reason = "AI 识别与推荐相关组件" }
    "com.xiaomi.aireco" = @{ Category = "推荐与内容流"; Risk = "低"; Action = "建议精简"; Reason = "小爱建议和桌面推荐内容" }
    "com.miui.personalassistant" = @{ Category = "推荐与内容流"; Risk = "低"; Action = "按需精简"; Reason = "负一屏/智能助理" }
    "com.miui.hybrid" = @{ Category = "推荐与内容流"; Risk = "中"; Action = "按需精简"; Reason = "Hybrid 容器，一些活动页和卡片可能依赖" }
    "com.miui.yellowpage" = @{ Category = "推荐与内容流"; Risk = "低"; Action = "建议精简"; Reason = "黄页与生活服务聚合" }
    "com.android.browser" = @{ Category = "预装应用"; Risk = "低"; Action = "建议精简"; Reason = "小米/安卓预装浏览器，可由第三方浏览器替代" }
    "com.miui.video" = @{ Category = "预装应用"; Risk = "低"; Action = "建议精简"; Reason = "预装视频应用" }
    "com.baidu.BaiduMap" = @{ Category = "预装应用"; Risk = "低"; Action = "按需精简"; Reason = "预装百度地图，不需要时可移除" }
    "com.xiaomi.market" = @{ Category = "应用分发"; Risk = "中"; Action = "按需精简"; Reason = "小米应用商店，移除后无法通过官方商店更新部分米系应用" }
    "com.xiaomi.minigame" = @{ Category = "游戏与娱乐"; Risk = "低"; Action = "建议精简"; Reason = "小游戏入口与分发组件" }
    "com.xiaomi.migameservice" = @{ Category = "游戏与娱乐"; Risk = "低"; Action = "建议精简"; Reason = "米系游戏服务框架" }
    "com.xiaomi.gamecenter.sdk.service" = @{ Category = "游戏与娱乐"; Risk = "低"; Action = "建议精简"; Reason = "游戏中心 SDK 服务" }
    "com.xiaomi.joyose" = @{ Category = "游戏与调度"; Risk = "中"; Action = "按需精简"; Reason = "性能调度与游戏场景优化，部分机型会影响帧率策略" }
    "com.miui.mishare.connectivity" = @{ Category = "互联互通"; Risk = "低"; Action = "按需精简"; Reason = "小米互传" }
    "com.xiaomi.mi_connect_service" = @{ Category = "互联互通"; Risk = "中"; Action = "按需精简"; Reason = "米家/跨设备互联服务" }
    "com.milink.service" = @{ Category = "互联互通"; Risk = "中"; Action = "按需精简"; Reason = "投屏与设备发现服务" }
    "com.miui.guardprovider" = @{ Category = "安全组件"; Risk = "高"; Action = "谨慎处理"; Reason = "安全检测提供者，可能影响安装校验和安全扫描" }
    "com.miui.accessibility" = @{ Category = "辅助功能"; Risk = "高"; Action = "谨慎处理"; Reason = "MIUI 辅助功能扩展，可能影响无障碍能力或自动化工具" }
    "com.xiaomi.mis" = @{ Category = "系统服务"; Risk = "中"; Action = "谨慎处理"; Reason = "小米系统服务，具体依赖会因机型和系统版本而异" }
    "com.mfashiongallery.emag" = @{ Category = "锁屏内容"; Risk = "低"; Action = "建议精简"; Reason = "小米画报/锁屏画报" }
    "com.miui.cleanmaster" = @{ Category = "系统清理"; Risk = "低"; Action = "建议精简"; Reason = "猎豹垃圾清理核心，常驻后台扫描与唤醒" }
    "com.android.quicksearchbox" = @{ Category = "系统搜索"; Risk = "低"; Action = "建议精简"; Reason = "桌面快捷搜索条常驻服务" }
    "com.miui.contentextension" = @{ Category = "辅助功能"; Risk = "低"; Action = "建议精简"; Reason = "传送门/内容扩展服务" }
    "com.miui.contentcatcher" = @{ Category = "推荐与内容流"; Risk = "低"; Action = "建议精简"; Reason = "屏幕内容抓取与分析服务" }
    "com.xiaomi.macro" = @{ Category = "系统辅助"; Risk = "低"; Action = "建议精简"; Reason = "自动连招/宏指令常驻服务" }
    "com.miui.thirdappassistant" = @{ Category = "应用增强"; Risk = "低"; Action = "建议精简"; Reason = "第三方应用辅助与悬浮插件" }
    "com.xiaomi.barrage" = @{ Category = "媒体娱乐"; Risk = "低"; Action = "建议精简"; Reason = "视频弹幕与浮窗组件" }
    "com.miui.compass" = @{ Category = "预装小工具"; Risk = "低"; Action = "按需精简"; Reason = "小米自带指南针" }
    "com.xiaomi.scanner" = @{ Category = "预装小工具"; Risk = "低"; Action = "按需精简"; Reason = "小米自带扫一扫" }
    "com.miui.cloudservice" = @{ Category = "云服务"; Risk = "中"; Action = "按需精简"; Reason = "小米云服务核心框架" }
    "com.miui.micloudsync" = @{ Category = "云服务"; Risk = "中"; Action = "按需精简"; Reason = "小米云数据同步" }
    "com.miui.cloudbackup" = @{ Category = "云服务"; Risk = "中"; Action = "按需精简"; Reason = "小米云备份组件" }
    "com.xiaomi.micloud.sdk" = @{ Category = "云服务"; Risk = "中"; Action = "按需精简"; Reason = "小米云服务 SDK 支撑服务" }
    "com.miui.voicetrigger" = @{ Category = "小爱与语音"; Risk = "低"; Action = "建议精简"; Reason = "小爱语音唤醒与麦克风监听底层服务" }
    "com.xiaomi.aiasst.vision" = @{ Category = "小爱与语音"; Risk = "低"; Action = "建议精简"; Reason = "小爱视觉/AI 图像与屏幕识别组件" }
    "com.miui.voiceassistoverlay" = @{ Category = "小爱与语音"; Risk = "低"; Action = "建议精简"; Reason = "语音助手动态波形与弹窗浮层资源" }
}

function Get-PackageSet {
    param(
        [string[]]$Arguments
    )

    $result = & adb shell pm list packages $Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "无法通过 adb 获取包列表，请确认设备已连接并已授权 USB 调试。"
    }

    $set = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $result) {
        $pkg = ($line -replace '^package:', '' -replace '\s+', '').Trim()
        if (-not [string]::IsNullOrWhiteSpace($pkg)) {
            [void]$set.Add($pkg)
        }
    }

    return $set
}

function Get-PackageUserState {
    param(
        [string]$PackageName
    )

    $result = & adb shell dumpsys package $PackageName 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "无法读取包状态：$PackageName"
    }

    $userLine = $result | Select-String -Pattern 'User 0:.*installed=(true|false).*enabled=([0-9]+)' | Select-Object -First 1
    if (-not $userLine) {
        return [PSCustomObject]@{
            Installed = $false
            Disabled = $false
        }
    }

    $installed = $userLine.Matches[0].Groups[1].Value -eq 'true'
    $enabledValue = [int]$userLine.Matches[0].Groups[2].Value

    return [PSCustomObject]@{
        Installed = $installed
        Disabled = $installed -and $enabledValue -eq 3
    }
}

function New-SectionTable {
    param(
        [string]$Title,
        [System.Collections.IEnumerable]$Items
    )

    $rows = @($Items)
    if ($rows.Count -eq 0) {
        return @("## $Title", "", "> 当前没有符合条件的条目。", "")
    }

    $lines = @(
        "## $Title",
        "",
        "| 包名 | 分类 | 风险 | 当前状态 | 说明 |",
        "|---|---|---|---|---|"
    )

    foreach ($item in $rows | Sort-Object Category, PackageName) {
        $lines += "| $($item.PackageName) | $($item.Category) | $($item.Risk) | $($item.State) | $($item.Reason) |"
    }

    $lines += ""
    return $lines
}

try {
    Write-Host "读取设备应用列表..."
    $allKnownPackages = Get-PackageSet @("-u")

    $removedTargets = New-Object System.Collections.Generic.List[object]
    $remainingTargets = New-Object System.Collections.Generic.List[object]

    foreach ($entry in $packageProfiles.GetEnumerator()) {
        $packageName = $entry.Key
        $profile = $entry.Value

        if (-not $allKnownPackages.Contains($packageName)) {
            continue
        }

        $record = [PSCustomObject]@{
            PackageName = $packageName
            Category = $profile.Category
            Risk = $profile.Risk
            Action = $profile.Action
            Reason = $profile.Reason
            State = "当前仍保留"
        }

        $userState = Get-PackageUserState -PackageName $packageName

        if ($userState.Installed) {
            if ($userState.Disabled) {
                $record.State = "已禁用"
                $removedTargets.Add($record)
                continue
            }

            $remainingTargets.Add($record)
        } else {
            $record.State = "已从用户 0 精简"
            $removedTargets.Add($record)
        }
    }

    $commandExamples = @(
        'adb shell pm uninstall -k --user 0 <包名>',
        'adb shell cmd package install-existing --user 0 <包名>',
        "adb shell pm list packages --user 0 | Select-String '<关键字>'"
    )

    $content = New-Object System.Collections.Generic.List[string]
    $content.Add('# 小米系统精简优化手册')
    $content.Add('')
    $content.Add("生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $content.Add('')
    $content.Add('这份手册基于当前设备 ADB 扫描结果生成，用来区分“已经精简”的组件，以及“仍然保留、可继续处理”的目标组件。')
    $content.Add('')
    $content.Add('## 使用原则')
    $content.Add('')
    $content.Add('- 优先处理广告、推荐、预装内容和游戏分发类组件。')
    $content.Add('- 涉及安全、互联互通、辅助功能的组件要先确认自己确实不用，再动。')
    $content.Add('- 统一使用 ADB 对用户 0 卸载，必要时可通过 install-existing 恢复。')
    $content.Add('')
    $content.Add('## 常用命令')
    $content.Add('')
    $content.Add('```powershell')
    foreach ($example in $commandExamples) {
        $content.Add($example)
    }
    $content.Add('```')
    $content.Add('')
    $content.Add('## 当前统计')
    $content.Add('')
    $content.Add("- 已完成精简：$($removedTargets.Count) 项")
    $content.Add("- 仍可继续评估：$($remainingTargets.Count) 项")
    $content.Add('')

    foreach ($line in (New-SectionTable -Title '已完成精简' -Items $removedTargets)) {
        $content.Add($line)
    }

    foreach ($line in (New-SectionTable -Title '仍可继续处理' -Items $remainingTargets)) {
        $content.Add($line)
    }

    $content.Add('## 建议执行顺序')
    $content.Add('')
    $content.Add('1. 先处理“广告与跟踪”“推荐与内容流”“预装应用”这三类。')
    $content.Add('2. 再决定是否处理“游戏与娱乐”“应用分发”“互联互通”。')
    $content.Add('3. “安全组件”“辅助功能”“系统服务”默认保守，只有明确不用时再处理。')
    $content.Add('')
    $content.Add('## 恢复说明')
    $content.Add('')
    $content.Add('如果某个组件精简后出现闪退、投屏失效、文件分享异常或系统设置缺项，优先执行恢复命令：')
    $content.Add('')
    $content.Add('```powershell')
    $content.Add('adb shell cmd package install-existing --user 0 <包名>')
    $content.Add('```')
    $content.Add('')
    $content.Add('恢复后建议重启一次手机，再验证对应功能。')

    $parentDir = Split-Path $outputFile -Parent
    if (-not (Test-Path $parentDir)) { New-Item -ItemType Directory -Path $parentDir -Force | Out-Null }

    Set-Content -Path $outputFile -Value $content -Encoding UTF8
    Write-Host "优化手册已生成：$outputFile" -ForegroundColor Green
}
catch {
    Write-Error $_
    exit 1
}
