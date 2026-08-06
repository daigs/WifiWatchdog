# WifiWatchdog

`WifiWatchdog` 是一个 Windows 服务：监控指定 Broadcom USB 无线网卡，自动连接 `TXRD-2.4G`；连续两次扫描不到任何 Wi-Fi 时，调用已验证有效的 `pnputil /restart-device` 重启网卡。

## 行为边界

- 扫描到其他 Wi-Fi、但扫描不到 `TXRD-2.4G`：只记录，不重启网卡。
- 已连接手机热点等其他 Wi-Fi：默认不切换。将 `ForceTargetWifi` 改为 `true` 才会强制连接目标 Wi-Fi。
- 扫描前会检查 Wi-Fi 软件开关；`AutoEnableWifi` 为 `true` 时，检测到关闭会先尝试打开再扫描。
- 如果是硬件射频开关关闭，程序不会强行重启网卡，会记录原因并等待人工打开。
- 连续两次无法扫描任何 Wi-Fi：重启配置中的指定 USB 设备。
- 一小时最多自动重启 3 次，且两次重启间隔至少 15 分钟，防止硬件故障时反复重置。
- 不执行 `CreateClearOldDriver.bat`，不删除任何驱动或 Wi-Fi 配置文件。

## 配置

发布目录中的 `appsettings.json` 包含网卡描述、USB 实例 ID、Wi-Fi 名称和密码。

`AutoEnableWifi` 控制服务是否在扫描前自动打开 Windows 的软件 Wi-Fi 开关，默认值为 `true`。

服务启动时会以 `LocalSystem` 身份创建或更新 Windows 的“所有用户” Wi-Fi Profile。密码不会写入日志，但会明文保存在该配置文件中；请只允许管理员修改发布目录和配置文件。

## 发布

项目已配置为 Native AOT、`win-x64`：

```powershell
dotnet publish -c Release -r win-x64
```

首次发布需要还原 Native AOT 与 Windows Service NuGet 包。发布后的程序不依赖目标机器已安装 .NET 运行时。

## 安装服务

以管理员 PowerShell 执行：

```powershell
.\scripts\Install-Service.ps1
```

查看运行状态：

```powershell
Get-Service WifiWatchdog
Get-Content "C:\ProgramData\WifiWatchdog\logs\wifi-watchdog-$(Get-Date -Format yyyyMMdd).log" -Tail 100
Get-Content "C:\ProgramData\WifiWatchdog\status.json"
```

卸载服务：

```powershell
.\scripts\Uninstall-Service.ps1
```

## 与现有 BroadcomWifiServer 的关系

电脑上已有第三方 `BroadcomWifiServer` 服务，也可能操作同一块 USB 无线网卡。不要长期让两个自动恢复服务同时工作；部署测试本服务前，建议先将旧服务停止并设为手动启动，确认本服务稳定后再决定保留哪个。
