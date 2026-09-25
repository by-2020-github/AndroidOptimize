using AndroidOptimize.Core.Adb;
using AndroidOptimize.Core.Apk;

namespace AndroidOptimize.Core.Testing;

/// <summary>
/// 演示模式用的「应用名读取器」：直接返回模拟设备上预置的名字，
/// 不去读真机 APK（演示模式下根本没有真机）。
/// </summary>
public sealed class DemoLabelSource(SimulatedAdbClient device)
    : ApkLabelSource(new AdbClient("simulated"))
{
    public override Task<string?> ReadLabelAsync(string packageName, CancellationToken ct)
        => Task.FromResult(device.Labels.TryGetValue(packageName, out var label) ? label : null);
}
