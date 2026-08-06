# WifiWatchdog

`WifiWatchdog` 是一个面向 Windows 的无线网络看门狗服务，用于监控指定 USB 无线网卡、自动打开 Windows Wi-Fi 软件开关、连接目标 Wi-Fi，并在无线网卡连续无法扫描到任何网络时自动重启网卡。

当前项目针对以下环境配置：

- 无线网卡：`Broadcom 802.11ac Wireless USB Adapter`
- 目标 Wi-Fi：`TXRD-2.4G`
- 运行平台：Windows x64
- 发布方式：.NET 10 Native AOT、自包含发布
- 服务名称：`WifiWatchdog`

## 工作流程

每次检查按以下顺序执行：

1. 查找配置中指定的无线网卡。
2. 查询 Wi-Fi 软件和硬件射频状态。
3. 软件 Wi-Fi 开关关闭时自动打开；硬件射频关闭时只记录状态，不重启网卡。
4. 启动时根据配置创建或更新 Windows 的“所有用户”Wi-Fi Profile。
5. 扫描附近 Wi-Fi，并确认目标 Wi-Fi 是否可见。
6. 目标 Wi-Fi 可见且当前未连接时，发起连接并验证结果。
7. 扫描到其他网络但扫描不到目标 Wi-Fi 时，只记录状态，不重启网卡。
8. 连续两次无法扫描到任何 Wi-Fi 时，通过以下命令重启指定网卡：

```powershell
pnputil /restart-device "USB\VID_0A5C&PID_BD27\000000000001"
```

程序不会删除驱动、不会执行 `CreateClearOldDriver.bat`，也不会重置 Winsock 或 TCP/IP。

## 连接行为

- `ForceTargetWifi` 为 `false` 时，如果已经连接手机热点或其他 Wi-Fi，服务不会主动断开当前连接。
- `ForceTargetWifi` 为 `true` 时，只要目标 Wi-Fi 可见，服务会尝试切换到目标 Wi-Fi。
- 已连接目标 Wi-Fi 时，服务仍会按照 `HealthScanIntervalMinutes` 定期执行健康扫描。
- 当前 Profile 创建逻辑使用 WPA2-PSK/AES，适用于当前 `TXRD-2.4G`。如果目标网络的安全类型发生变化，需要同步调整 Profile 配置。

## 配置说明

配置文件为程序目录中的 `appsettings.json`。修改配置后需要重启 `WifiWatchdog` 服务才能生效。

| 配置项 | 当前值 | 说明 |
| --- | --- | --- |
| `AdapterDescription` | `Broadcom 802.11ac Wireless USB Adapter` | Windows WLAN API 中显示的无线网卡描述，用于定位网卡。 |
| `AdapterInstanceId` | `USB\VID_0A5C&PID_BD27\000000000001` | 设备实例 ID，用于执行 `pnputil /restart-device`。 |
| `TargetSsid` | `TXRD-2.4G` | 要自动连接的 Wi-Fi 名称。 |
| `TargetProfileName` | `TXRD-2.4G` | Windows 保存的 Wi-Fi Profile 名称，通常与 SSID 相同。 |
| `TargetPassword` | 已配置 | 目标 Wi-Fi 密码，明文保存在配置文件中，不会写入日志。 |
| `ProvisionProfileOnStart` | `true` | 服务启动时是否创建或更新 Wi-Fi Profile。 |
| `OverwriteExistingProfileOnStart` | `true` | 是否覆盖同名 Profile，并使用当前配置中的密码和安全设置。 |
| `ConnectToHiddenNetwork` | `false` | 目标 Wi-Fi 是否为隐藏网络。 |
| `ForceTargetWifi` | `false` | 是否强制断开其他 Wi-Fi 并切换到目标 Wi-Fi。 |
| `AutoEnableWifi` | `true` | 扫描前发现 Windows Wi-Fi 软件开关关闭时是否自动打开。 |
| `CheckIntervalSeconds` | `30` | 主循环检查间隔，单位为秒。 |
| `HealthScanIntervalMinutes` | `1` | 已连接 Wi-Fi 时的健康扫描间隔，单位为分钟。 |
| `ScanWaitSeconds` | `4` | 发起扫描后等待 WLAN API 更新结果的时间，单位为秒。 |
| `EmptyScanRetryDelaySeconds` | `8` | 第一次扫描不到任何网络后，再次确认前的等待时间，单位为秒。 |
| `ConnectVerificationWaitSeconds` | `12` | 发起连接后等待验证结果的时间，单位为秒。 |
| `ConnectCooldownSeconds` | `20` | 两次自动连接尝试之间的最短间隔，单位为秒。 |
| `AdapterRestartCooldownMinutes` | `1` | 两次自动重启网卡之间的最短间隔，单位为分钟。 |
| `MaxAdapterRestartsPerHour` | `60` | 一小时内允许自动重启网卡的最大次数。 |
| `AdapterRestartSettleSeconds` | `10` | 重启网卡后等待设备重新枚举的时间，单位为秒。 |

`TargetPassword` 是明文配置。部署目录应仅允许管理员和 `LocalSystem` 访问，避免普通用户修改或读取配置。

## 发布

项目已经配置为 `net10.0-windows`、`win-x64`、Native AOT 和自包含发布：

```powershell
dotnet publish -c Release -r win-x64
```

首次发布需要能够还原 NuGet 包，并安装 Native AOT 所需的 Windows C++ 构建工具。发布后的程序不要求目标电脑预装 .NET 运行时。

当前使用的发布目录为：

```text
E:\Projects\WifiWatchdog\bin\Release\net10.0-windows\win-x64\publish\win-x64
```

目录结构：

```text
win-x64\
├── WifiWatchdog.exe
├── appsettings.json
└── scripts\
    ├── Install-Service.ps1
    └── Uninstall-Service.ps1
```

## 安装服务

以管理员身份打开 PowerShell，进入包含 `WifiWatchdog.exe` 的发布目录：

```powershell
cd E:\Projects\WifiWatchdog\bin\Release\net10.0-windows\win-x64\publish\win-x64
.\scripts\Install-Service.ps1
```

安装脚本通过 `$PSScriptRoot` 自动定位上一级的 `WifiWatchdog.exe`，不需要传入任何发布路径。

安装后服务具有以下设置：

- 启动类型：延迟自动启动
- 运行账户：`LocalSystem`
- 依赖服务：`WlanSvc`
- 服务异常退出时：60 秒后自动重启，最多连续执行两次恢复动作

检查服务状态：

```powershell
Get-Service WifiWatchdog
sc.exe qc WifiWatchdog
```

正常运行时，`Get-Service` 应显示 `Running`。

## 日志和状态

日志保存在程序目录下。以当前发布目录为例：

```text
E:\Projects\WifiWatchdog\bin\Release\net10.0-windows\win-x64\publish\win-x64\WifiWatchdog\logs\wifi-watchdog-yyyyMMdd.log
E:\Projects\WifiWatchdog\bin\Release\net10.0-windows\win-x64\publish\win-x64\WifiWatchdog\status.json
```

在发布目录中查看最新日志和状态：

```powershell
Get-Content ".\WifiWatchdog\logs\wifi-watchdog-$(Get-Date -Format yyyyMMdd).log" -Tail 100
Get-Content ".\WifiWatchdog\status.json"
```

`status.json` 使用 UTF-8 编码，中文消息会直接显示，不会写成 `\uXXXX` 转义形式。

常见状态：

| 状态 | 含义 |
| --- | --- |
| `Started` | 服务已启动。 |
| `WifiEnabled` | 检测到软件 Wi-Fi 开关关闭，已经自动打开。 |
| `WifiDisabled` | 硬件射频处于关闭状态，程序无法自动打开。 |
| `WifiEnableFailed` | 打开软件 Wi-Fi 开关失败。 |
| `ProfileReady` | Wi-Fi Profile 已确认或更新。 |
| `ConnectedTarget` | 已连接目标 Wi-Fi。 |
| `ConnectedOther` | 当前连接其他 Wi-Fi，配置不允许强制切换。 |
| `TargetNotVisible` | 扫描功能正常，但没有发现目标 Wi-Fi。 |
| `ScanFailed` | 连续两次无法正常扫描 Wi-Fi。 |
| `AdapterRestarted` | 已通过 `pnputil` 重启无线网卡。 |
| `RestartCooldown` | 扫描异常，但网卡仍处于重启冷却时间。 |
| `RestartLimitReached` | 一小时内网卡重启次数达到上限。 |
| `ConnectFailed` | 请求连接失败，或请求后仍未连接目标 Wi-Fi。 |
| `UnexpectedError` | 监控循环出现未预期异常。 |

## 更新和卸载

更新程序时，先卸载旧服务，再替换发布目录内容并重新安装：

```powershell
.\scripts\Uninstall-Service.ps1
.\scripts\Install-Service.ps1
```

只修改 `appsettings.json` 时，不需要卸载服务，重启即可：

```powershell
Restart-Service WifiWatchdog
```

完全卸载服务：

```powershell
.\scripts\Uninstall-Service.ps1
```

卸载脚本只删除 Windows 服务，不删除程序、配置、日志或无线网卡驱动。

## 与 BroadcomWifiServer 的关系

电脑上已有第三方 `BroadcomWifiServer` 服务，它也可能操作同一块 Broadcom USB 无线网卡。不要让两个自动恢复服务长期同时运行。部署 `WifiWatchdog` 前，建议停止 `BroadcomWifiServer` 并将其启动类型设为手动，确认本服务稳定后再决定保留哪个。
