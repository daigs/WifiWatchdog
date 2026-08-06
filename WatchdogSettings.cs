using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WifiWatchdog;

public sealed class WatchdogSettings
{
    /// <summary>无线网卡的设备描述，用于在系统列出的无线接口中定位网卡。</summary>
    public string AdapterDescription { get; init; } = "Broadcom 802.11ac Wireless USB Adapter";

    /// <summary>无线网卡的设备实例 ID，用于执行 pnputil 重启网卡。</summary>
    public string AdapterInstanceId { get; init; } = "USB\\VID_0A5C&PID_BD27\\000000000001";

    /// <summary>要自动连接的 Wi-Fi 名称，也就是 SSID。</summary>
    public string TargetSsid { get; init; } = "TXRD-2.4G";

    /// <summary>Windows 中保存的 Wi-Fi 配置名称，通常与 TargetSsid 保持一致。</summary>
    public string TargetProfileName { get; init; } = "TXRD-2.4G";

    /// <summary>目标 Wi-Fi 密码。仅在 ProvisionProfileOnStart 为 true 时必须填写。</summary>
    public string TargetPassword { get; init; } = string.Empty;

    /// <summary>服务启动后是否根据本文件创建或更新 Windows 的 Wi-Fi 配置。</summary>
    public bool ProvisionProfileOnStart { get; init; } = true;

    /// <summary>是否覆盖同名的已有 Wi-Fi 配置。true 时会使用本文件中的密码和安全设置。</summary>
    public bool OverwriteExistingProfileOnStart { get; init; } = true;

    /// <summary>目标 Wi-Fi 是否为隐藏网络。普通可见 Wi-Fi 保持 false。</summary>
    public bool ConnectToHiddenNetwork { get; init; }

    /// <summary>是否强制切换到目标 Wi-Fi。false 时不会主动断开手机热点等当前连接。</summary>
    public bool ForceTargetWifi { get; init; }

    /// <summary>扫描前是否自动打开 Windows 的软件 Wi-Fi 开关。</summary>
    public bool AutoEnableWifi { get; init; } = true;

    /// <summary>主循环检查间隔，单位为秒。</summary>
    public int CheckIntervalSeconds { get; init; } = 30;

    /// <summary>对已连接 Wi-Fi 进行健康扫描的间隔，单位为分钟。</summary>
    public int HealthScanIntervalMinutes { get; init; } = 1;

    /// <summary>发起 Wi-Fi 扫描后等待结果的时间，单位为秒。</summary>
    public int ScanWaitSeconds { get; init; } = 4;

    /// <summary>第一次扫描不到任何 Wi-Fi 后，等待再次确认的时间，单位为秒。</summary>
    public int EmptyScanRetryDelaySeconds { get; init; } = 8;

    /// <summary>发起连接后等待验证是否成功的时间，单位为秒。</summary>
    public int ConnectVerificationWaitSeconds { get; init; } = 12;

    /// <summary>两次自动连接尝试之间的最短间隔，单位为秒。</summary>
    public int ConnectCooldownSeconds { get; init; } = 20;

    /// <summary>两次自动重启无线网卡之间的最短间隔，单位为分钟。</summary>
    public int AdapterRestartCooldownMinutes { get; init; } = 1;

    /// <summary>一小时内允许自动重启无线网卡的最大次数。</summary>
    public int MaxAdapterRestartsPerHour { get; init; } = 60;

    /// <summary>重启无线网卡后等待设备重新枚举的时间，单位为秒。</summary>
    public int AdapterRestartSettleSeconds { get; init; } = 10;

    public static WatchdogSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("未找到 WiFiWatchdog 配置文件。", path);
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize(json, WatchdogJsonContext.Default.WatchdogSettings)
            ?? throw new InvalidOperationException("WiFiWatchdog 配置文件为空或格式无效。");
    }

    public void Validate()
    {
        Require(AdapterDescription, nameof(AdapterDescription));
        Require(AdapterInstanceId, nameof(AdapterInstanceId));
        Require(TargetSsid, nameof(TargetSsid));
        Require(TargetProfileName, nameof(TargetProfileName));

        if (ProvisionProfileOnStart)
        {
            Require(TargetPassword, nameof(TargetPassword));
        }

        if (Encoding.UTF8.GetByteCount(TargetSsid) > 32)
        {
            throw new InvalidOperationException("TargetSsid 的 UTF-8 长度不能超过 32 字节。");
        }

        RequireRange(CheckIntervalSeconds, 10, 3600, nameof(CheckIntervalSeconds));
        RequireRange(HealthScanIntervalMinutes, 1, 1440, nameof(HealthScanIntervalMinutes));
        RequireRange(ScanWaitSeconds, 1, 30, nameof(ScanWaitSeconds));
        RequireRange(EmptyScanRetryDelaySeconds, 1, 120, nameof(EmptyScanRetryDelaySeconds));
        RequireRange(ConnectVerificationWaitSeconds, 1, 120, nameof(ConnectVerificationWaitSeconds));
        RequireRange(ConnectCooldownSeconds, 1, 3600, nameof(ConnectCooldownSeconds));
        RequireRange(AdapterRestartCooldownMinutes, 1, 1440, nameof(AdapterRestartCooldownMinutes));
        RequireRange(MaxAdapterRestartsPerHour, 1, 60, nameof(MaxAdapterRestartsPerHour));
        RequireRange(AdapterRestartSettleSeconds, 1, 120, nameof(AdapterRestartSettleSeconds));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"配置项 {name} 不能为空。");
        }
    }

    private static void RequireRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new InvalidOperationException($"配置项 {name} 必须在 {min} 到 {max} 之间。");
        }
    }
}

public sealed class WatchdogStatus
{
    public DateTimeOffset Timestamp { get; init; }

    public string State { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public int? VisibleBssCount { get; init; }

    public int? TargetBssCount { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WatchdogSettings))]
[JsonSerializable(typeof(WatchdogStatus))]
internal partial class WatchdogJsonContext : JsonSerializerContext
{
}