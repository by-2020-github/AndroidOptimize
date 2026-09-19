# legacy：已被桌面版取代的 PowerShell 脚本

这个目录是 AndroidOptimize **桌面版之前**的脚本工具箱，整体保留下来备查与追溯。

它们**不再是这个项目的主线**。功能已经在桌面版里重新实现过，而且做得更完整
（有预览、有校验、有回滚）。下面的对照表说明每个脚本现在由什么负责。

> 只想用桌面版的话，**整个 `legacy/` 目录可以直接删掉**，版本历史里仍然可以找回。

---

## 新旧对照表

| 旧脚本 | 现在由什么负责 |
|---|---|
| `apply_all_optimizations.ps1` | 桌面版的「连接 → 扫描 → 计划 → 执行」主流程 |
| `devices/xiaomi/optimize_miui.ps1` | [`data/packages.json`](../data/packages.json) 里的小米系规则 |
| `devices/xiaomi/restore_guardprovider.ps1` | 桌面版的「回滚」页；而且 `com.miui.guardprovider` 现在是**绝对保护项**，不会再被误删 |
| `devices/xiaomi/generate_md.ps1` | 桌面版的「导出体检报告」 |
| `system/optimize_animations.ps1` | [`data/packages.json`](../data/packages.json) 的 `settings` 段 |
| `apps/optimize_wechat.ps1` | `packages.json` 的微信规则（现在默认在保护名单里，需要手动解锁才会出现） |
| `apps/optimize_commercial_apps.ps1` | `packages.json` 的 `restrict` 规则（淘宝/京东/美团/拼多多） |
| `apps/optimize_bytedance.ps1` | `packages.json` 的 `restrict` 规则（抖音/快手） |
| `core/adb_helper.ps1` | 桌面版的 `AdbLocator` + `AdbBootstrap`（内置 adb 自解压） |

### 为什么换掉

老脚本是「一条命令直接改手机」，没有预览、没有快照、没有结果校验。
桌面版改成**先快照 → 再执行 → 回读校验 → 可一键回滚**，这是给不特定用户使用的必要前提。

另外几处具体的修正：

- `optimize_miui.ps1` 原本会精简 `com.miui.guardprovider`（安装校验组件），
  这会导致安装应用时反复报异常——桌面版把它列进了保护名单。
- `optimize_wechat.ps1` 里的 `WAKE_LOCK ignore` 会让微信在锁屏后收不到消息，
  桌面版把它降级成需要手动开启的「深度模式」选项。
- `optimize_animations.ps1` 把刷新率锁死在 120Hz 会增加耗电，
  桌面版改成默认不勾选的可选项。

---

## 桌面版还没有对应功能的两个脚本

这两个脚本的功能**桌面板暂时没有实现**，所以它们仍然是可以用的独立工具。

| 脚本 | 功能 | 说明 |
|---|---|---|
| [`analyze_heat.ps1`](analyze_heat.ps1) | 3 分钟发热探针 | 每秒采样一次 `top`，聚合出高 CPU 占用的进程，输出到 `reports/` |
| [`clean_memory.ps1`](clean_memory.ps1) | 一键清理后台 | `am kill-all` + 强停常见重度应用 + 触发系统内存整理 |

> 如果你需要，可以把这两个功能也移植进桌面版（`clean_memory` 尤其适合做成一个按钮）。

---

## 怎么运行这些脚本

脚本之间的相对路径已经按新位置调整过，在**仓库根目录**下直接执行即可：

```powershell
# 一键全自动优化（会直接改手机，没有预览和回滚，请谨慎）
.\legacy\apply_all_optimizations.ps1

# 单独运行
.\legacy\analyze_heat.ps1
.\legacy\clean_memory.ps1
.\legacy\apps\optimize_wechat.ps1
```

它们会自动使用 [`vendor/platform-tools/adb.exe`](../vendor/platform-tools)。

---

## 目录内容

```text
legacy/
├── apply_all_optimizations.ps1   旧的主入口
├── analyze_heat.ps1              发热探针（桌面版暂无）
├── clean_memory.ps1              一键清理后台（桌面版暂无）
├── apps/                         微信 / 电商 / 字节系专项脚本
├── core/adb_helper.ps1           ADB 路径发现
├── devices/                      各品牌精简脚本与手册
├── system/optimize_animations.ps1 动效与高刷
├── reports/                      旧脚本产生的报告（历史输出）
└── apks/                         与本工具无关的 APK，建议单独处理
```
