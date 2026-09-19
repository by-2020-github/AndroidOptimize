# 🌡️ 3分钟实时发热与性能异常审查报告

> **生成时间**：2026-04-25 13:42:35
> **测试时长**：3 分钟
> **分析逻辑**：通过在 3 分钟内发起多次 CPU 芯片切片探测，筛选出这期间高频唤醒、运算死循环或霸占计算资源的“毒瘤”应用。通常 CPU 平均占用率 > **10%** 且长时间不休眠的即为发热元凶。

## ⚠️ 高嫌疑计算资源消耗榜单 (重点研判对象)
| 进程/组件名 (Process) | 3分钟内平均CPU | 检测峰值CPU | 抓取活跃频次 | 诊断与处理建议 |
|---|---|---|---|---|
| \$(@{Process=com.tencent.mm; Avg=125.24; Max=321; Samp=180}.Process)\ | **125.24%** | 321% | 180/180 | 🔥 **严重发热核心**：持续大量霸占算力，建议立刻结束运行并限制后台 |
| \$(@{Process=camerahalserver; Avg=75.27; Max=110; Samp=180}.Process)\ | **75.27%** | 110% | 180/180 | 🔥 **严重发热核心**：持续大量霸占算力，建议立刻结束运行并限制后台 |
| \$(@{Process=surfaceflinger; Avg=37.46; Max=110; Samp=180}.Process)\ | **37.46%** | 110% | 180/180 | 🔥 **严重发热核心**：持续大量霸占算力，建议立刻结束运行并限制后台 |
| \$(@{Process=system_server; Avg=27.09; Max=142; Samp=173}.Process)\ | **27.09%** | 142% | 173/180 | ⚠️ **频发高负载**：代码可能存在暴力唤醒或重绘异常，容易累积发热 |
| \$(@{Process=android.hardware.media.c2@1.2-mediatek-64b; Avg=15.43; Max=26.6; Samp=178}.Process)\ | **15.43%** | 26.6% | 178/180 | 安卓底层绘制组件 (通常是替被点亮的前台应用处理画面) |
| \$(@{Process=top -b -n 1 -m 20; Avg=15.2; Max=23.3; Samp=180}.Process)\ | **15.2%** | 23.3% | 180/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=com.follow.clash:remote; Avg=13.64; Max=38.7; Samp=178}.Process)\ | **13.64%** | 38.7% | 178/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=android.hardware.graphics.composer@3.1-service; Avg=11.34; Max=36.6; Samp=174}.Process)\ | **11.34%** | 36.6% | 174/180 | 安卓底层绘制组件 (通常是替被点亮的前台应用处理画面) |
| \$(@{Process=com.android.systemui; Avg=11.25; Max=110; Samp=117}.Process)\ | **11.25%** | 110% | 117/180 | ⚠️ **频发高负载**：代码可能存在暴力唤醒或重绘异常，容易累积发热 |
| \$(@{Process=cameraserver; Avg=9.43; Max=16.6; Samp=170}.Process)\ | **9.43%** | 16.6% | 170/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=[vdec_ipi_recv]; Avg=7.15; Max=13.3; Samp=148}.Process)\ | **7.15%** | 13.3% | 148/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=android.hardware.audio.service.mediatek; Avg=5.95; Max=10; Samp=141}.Process)\ | **5.95%** | 10% | 141/180 | 安卓底层绘制组件 (通常是替被点亮的前台应用处理画面) |
| \$(@{Process=audioserver; Avg=4.97; Max=11.1; Samp=134}.Process)\ | **4.97%** | 11.1% | 134/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=logd; Avg=4.17; Max=20; Samp=104}.Process)\ | **4.17%** | 20% | 104/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=com.google.android.inputmethod.latin; Avg=2.66; Max=90; Samp=20}.Process)\ | **2.66%** | 90% | 20/180 | ⚠️ **频发高负载**：代码可能存在暴力唤醒或重绘异常，容易累积发热 |
| \$(@{Process=com.miui.home; Avg=2.6; Max=78.5; Samp=13}.Process)\ | **2.6%** | 78.5% | 13/180 | ⚠️ **频发高负载**：代码可能存在暴力唤醒或重绘异常，容易累积发热 |
| \$(@{Process=com.tencent.mm:appbrand1; Avg=2.09; Max=46.8; Samp=74}.Process)\ | **2.09%** | 46.8% | 74/180 | ⚠️ **频发高负载**：代码可能存在暴力唤醒或重绘异常，容易累积发热 |
| \$(@{Process=com.tencent.mm:push; Avg=1.58; Max=37; Samp=31}.Process)\ | **1.58%** | 37% | 31/180 | 通讯应用高强度轮询与网络保活，重灾区 |
| \$(@{Process=com.android.phone; Avg=1.43; Max=68.4; Samp=28}.Process)\ | **1.43%** | 68.4% | 28/180 | ⚠️ **频发高负载**：代码可能存在暴力唤醒或重绘异常，容易累积发热 |
| \$(@{Process=cn.litiaotiao.app; Avg=1.2; Max=17.2; Samp=33}.Process)\ | **1.2%** | 17.2% | 33/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=com.tencent.mm:xweb_sandboxed_process_0:com.tencent.xweb.pinus.sdk.process.SandboxedProcessServi; Avg=1.1; Max=86.6; Samp=11}.Process)\ | **1.1%** | 86.6% | 11/180 | ⚠️ **频发高负载**：代码可能存在暴力唤醒或重绘异常，容易累积发热 |
| \$(@{Process=com.android.camera; Avg=0.89; Max=92.5; Samp=4}.Process)\ | **0.89%** | 92.5% | 4/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=com.android.settings:remote; Avg=0.65; Max=66.6; Samp=3}.Process)\ | **0.65%** | 66.6% | 3/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=com.tencent.mm:appbrand0; Avg=0.54; Max=56.6; Samp=12}.Process)\ | **0.54%** | 56.6% | 12/180 | ⚠️ **频发高负载**：代码可能存在暴力唤醒或重绘异常，容易累积发热 |
| \$(@{Process=com.android.vending; Avg=0.46; Max=50; Samp=3}.Process)\ | **0.46%** | 50% | 3/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=[kcompactd0]; Avg=0.43; Max=47; Samp=3}.Process)\ | **0.43%** | 47% | 3/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=com.google.android.gms; Avg=0.4; Max=72.4; Samp=1}.Process)\ | **0.4%** | 72.4% | 1/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=com.android.vending:background; Avg=0.39; Max=59.3; Samp=3}.Process)\ | **0.39%** | 59.3% | 3/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=com.android.bluetooth; Avg=0.33; Max=23.3; Samp=3}.Process)\ | **0.33%** | 23.3% | 3/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=com.xiaomi.finddevice; Avg=0.24; Max=21.8; Samp=2}.Process)\ | **0.24%** | 21.8% | 2/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=[kswapd0]; Avg=0.24; Max=42.8; Samp=1}.Process)\ | **0.24%** | 42.8% | 1/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=mtd_mitee@1.3; Avg=0.23; Max=40.6; Samp=1}.Process)\ | **0.23%** | 40.6% | 1/180 | 正常系统环境响应/无害波动 |
| \$(@{Process=com.xiaomi.account; Avg=0.15; Max=23.3; Samp=2}.Process)\ | **0.15%** | 23.3% | 2/180 | 正常系统环境响应/无害波动 |

---
*提示：排在前三的高耗电应用，建议划脱多任务强行结束，或在电池中心开启【后台智能限制】*
