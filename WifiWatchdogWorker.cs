using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace WifiWatchdog;

public sealed class WifiWatchdogWorker(WatchdogSettings settings, WatchdogLog log) : BackgroundService
{
    private readonly Queue<DateTimeOffset> _adapterRestartTimes = new();

    private Guid? _profileConfiguredForInterface;
    private DateTimeOffset _lastHealthScanAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastConnectAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAdapterRestartAt = DateTimeOffset.MinValue;
    private string? _lastReportedState;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Report("Started", "WiFi 看门狗服务已启动。", force: true);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Report("UnexpectedError", exception.Message, force: true);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.CheckIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        Report("Stopped", "WiFi 看门狗服务已停止。", force: true);
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var wlan = WlanClient.Open();
        var wirelessInterface = wlan.FindInterface(settings.AdapterDescription);

        if (wirelessInterface is null)
        {
            Report("AdapterMissing", $"未找到无线网卡：{settings.AdapterDescription}", force: false);
            return;
        }

        if (settings.AutoEnableWifi)
        {
            var radioState = wlan.GetRadioState(wirelessInterface);
            if (!radioState.IsKnown)
            {
                Report("RadioStateUnknown", $"无法读取 Wi-Fi 开关状态，错误码={radioState.QueryError}；继续尝试扫描。", force: false);
            }
            else if (radioState.HardwareRadioOff)
            {
                Report("WifiDisabled", "Wi-Fi 硬件射频开关处于关闭状态，程序无法自动打开，请检查物理开关或系统设置。", force: true);
                return;
            }
            else if (radioState.SoftwareRadioOff)
            {
                var enableError = wlan.EnableSoftwareRadio(wirelessInterface, radioState.SoftwareOffPhyIndex);
                if (enableError != 0)
                {
                    Report("WifiEnableFailed", $"尝试打开 Wi-Fi 软件开关失败，错误码={enableError}。", force: true);
                    return;
                }

                Report("WifiEnabled", "检测到 Wi-Fi 已关闭，已自动打开无线开关。", force: true);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        if (_profileConfiguredForInterface != wirelessInterface.InterfaceGuid)
        {
            wlan.EnsureTargetProfile(wirelessInterface, settings);
            _profileConfiguredForInterface = wirelessInterface.InterfaceGuid;
            Report("ProfileReady", $"已确认 Wi-Fi 配置：{settings.TargetProfileName}", force: true);
        }

        var currentSsid = wlan.GetCurrentConnectionSsid(wirelessInterface);
        var targetConnected = string.Equals(currentSsid, settings.TargetSsid, StringComparison.Ordinal);
        var healthScanDue = DateTimeOffset.UtcNow - _lastHealthScanAt >= TimeSpan.FromMinutes(settings.HealthScanIntervalMinutes);

        if (targetConnected && !healthScanDue)
        {
            Report("ConnectedTarget", $"已连接 {settings.TargetSsid}。", force: false);
            return;
        }

        if (currentSsid is not null && !settings.ForceTargetWifi && !healthScanDue)
        {
            Report("ConnectedOther", $"当前连接 {currentSsid}；按配置不强制切换目标 Wi-Fi。", force: false);
            return;
        }

        var scan = await ScanAsync(wlan, wirelessInterface, cancellationToken);
        _lastHealthScanAt = DateTimeOffset.UtcNow;

        if (!scan.CanSearchNetworks)
        {
            var confirmedScan = await ConfirmFailedScanAsync(wlan, wirelessInterface, cancellationToken);
            if (!confirmedScan.CanSearchNetworks)
            {
                Report(
                    "ScanFailed",
                    $"连续两次扫描异常。扫描错误={confirmedScan.ScanError}，可见 BSS={confirmedScan.VisibleBssCount}。",
                    confirmedScan.VisibleBssCount,
                    confirmedScan.TargetBssCount,
                    force: true);
                await TryRestartAdapterAsync(cancellationToken);
                return;
            }

            scan = confirmedScan;
        }

        if (targetConnected)
        {
            Report("ConnectedTarget", $"已连接 {settings.TargetSsid}，健康扫描正常。", scan.VisibleBssCount, scan.TargetBssCount, force: false);
            return;
        }

        if (scan.TargetBssCount == 0)
        {
            Report(
                "TargetNotVisible",
                $"无线网卡扫描正常（可见 {scan.VisibleBssCount} 个 BSS），但未发现 {settings.TargetSsid}；不重启网卡。",
                scan.VisibleBssCount,
                scan.TargetBssCount,
                force: false);
            return;
        }

        if (currentSsid is not null && !settings.ForceTargetWifi)
        {
            Report("ConnectedOther", $"当前连接 {currentSsid}；目标 Wi-Fi 可见但未强制切换。", scan.VisibleBssCount, scan.TargetBssCount, force: false);
            return;
        }

        if (DateTimeOffset.UtcNow - _lastConnectAttemptAt < TimeSpan.FromSeconds(settings.ConnectCooldownSeconds))
        {
            Report("ConnectCooldown", "目标 Wi-Fi 可见，等待上一次连接尝试的冷却时间。", scan.VisibleBssCount, scan.TargetBssCount, force: false);
            return;
        }

        _lastConnectAttemptAt = DateTimeOffset.UtcNow;
        var connectError = wlan.ConnectToProfile(wirelessInterface, settings.TargetProfileName);
        if (connectError != 0)
        {
            Report("ConnectFailed", $"请求连接 {settings.TargetSsid} 失败，错误码={connectError}。", scan.VisibleBssCount, scan.TargetBssCount, force: true);
            return;
        }

        Report("ConnectRequested", $"已请求连接 {settings.TargetSsid}。", scan.VisibleBssCount, scan.TargetBssCount, force: true);
        await Task.Delay(TimeSpan.FromSeconds(settings.ConnectVerificationWaitSeconds), cancellationToken);

        var connectedSsid = wlan.GetCurrentConnectionSsid(wirelessInterface);
        if (string.Equals(connectedSsid, settings.TargetSsid, StringComparison.Ordinal))
        {
            Report("ConnectedTarget", $"已自动连接 {settings.TargetSsid}。", force: true);
        }
        else
        {
            Report("ConnectFailed", $"连接请求后仍未连接 {settings.TargetSsid}。", force: true);
        }
    }

    private async Task<WifiScanResult> ScanAsync(WlanClient wlan, WlanInterface wirelessInterface, CancellationToken cancellationToken)
    {
        var scanError = wlan.BeginScan(wirelessInterface);
        if (scanError == 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(settings.ScanWaitSeconds), cancellationToken);
        }

        return wlan.ReadScanResult(wirelessInterface, settings.TargetSsid, scanError);
    }

    private async Task<WifiScanResult> ConfirmFailedScanAsync(WlanClient wlan, WlanInterface wirelessInterface, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(settings.EmptyScanRetryDelaySeconds), cancellationToken);
        return await ScanAsync(wlan, wirelessInterface, cancellationToken);
    }

    private async Task TryRestartAdapterAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        while (_adapterRestartTimes.Count > 0 && now - _adapterRestartTimes.Peek() > TimeSpan.FromHours(1))
        {
            _adapterRestartTimes.Dequeue();
        }

        if (_adapterRestartTimes.Count >= settings.MaxAdapterRestartsPerHour)
        {
            Report("RestartLimitReached", "一小时内重启网卡次数已达上限，停止自动重启，建议检查网卡发热或更换网卡。", force: true);
            return;
        }

        if (now - _lastAdapterRestartAt < TimeSpan.FromMinutes(settings.AdapterRestartCooldownMinutes))
        {
            Report("RestartCooldown", "扫描异常，但网卡重启仍在冷却时间内。", force: false);
            return;
        }

        var pnputilPath = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = pnputilPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/restart-device");
        startInfo.ArgumentList.Add(settings.AdapterInstanceId);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 pnputil.exe。");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            Report("RestartFailed", $"pnputil 重启网卡失败，退出码={process.ExitCode}：{TrimProcessOutput(detail)}", force: true);
            return;
        }

        _lastAdapterRestartAt = now;
        _adapterRestartTimes.Enqueue(now);
        _profileConfiguredForInterface = null;
        Report("AdapterRestarted", "已通过 pnputil 重启无线网卡，等待设备恢复。", force: true);
        await Task.Delay(TimeSpan.FromSeconds(settings.AdapterRestartSettleSeconds), cancellationToken);
    }

    private void Report(string state, string message, int? visibleBssCount = null, int? targetBssCount = null, bool force = false)
    {
        if (force || !string.Equals(_lastReportedState, state, StringComparison.Ordinal))
        {
            log.Write("INFO", state, message, visibleBssCount, targetBssCount);
            _lastReportedState = state;
        }
    }

    private static string TrimProcessOutput(string output)
    {
        var compact = output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 300 ? compact : compact[..300];
    }
}
