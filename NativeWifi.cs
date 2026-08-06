using System.Runtime.InteropServices;
using System.Security;
using System.Text;

// ReSharper disable ConstantNullCoalescingCondition
// ReSharper disable MemberHidesStaticFromOuterClass

namespace WifiWatchdog;

internal sealed class WlanClient : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorFileNotFound = 2;
    private const uint ErrorNotFound = 1168;
    private const uint ErrorInvalidData = 13;
    private const int WlanConnectionAttributesSsidOffset = 520;
    private const int Dot11SsidBytesOffset = 4;
    private const int WlanRadioStateHeaderSize = sizeof(uint);
    private const int WlanPhyRadioStateSize = sizeof(uint) + sizeof(int) * 2;

    private IntPtr _clientHandle;

    private WlanClient(IntPtr clientHandle)
    {
        _clientHandle = clientHandle;
    }

    public static WlanClient Open()
    {
        var result = WlanOpenHandle(2, IntPtr.Zero, out _, out var clientHandle);
        ThrowIfFailed(result, "WlanOpenHandle");
        return new WlanClient(clientHandle);
    }

    public WlanInterface? FindInterface(string adapterDescription)
    {
        var result = WlanEnumInterfaces(_clientHandle, IntPtr.Zero, out var interfaceList);
        ThrowIfFailed(result, "WlanEnumInterfaces");

        try
        {
            var count = Marshal.ReadInt32(interfaceList, 0);
            var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
            var firstItemOffset = sizeof(uint) * 2;

            for (var index = 0; index < count; index++)
            {
                var itemPointer = IntPtr.Add(interfaceList, firstItemOffset + index * itemSize);
                var item = Marshal.PtrToStructure<WlanInterfaceInfo>(itemPointer);

                if (string.Equals(item.Description, adapterDescription, StringComparison.Ordinal))
                {
                    return new WlanInterface(item.InterfaceGuid, item.Description, item.State);
                }
            }

            return null;
        }
        finally
        {
            WlanFreeMemory(interfaceList);
        }
    }

    public string? GetCurrentConnectionSsid(WlanInterface wirelessInterface)
    {
        var interfaceGuid = wirelessInterface.InterfaceGuid;
        var result = WlanQueryInterface(
            _clientHandle,
            ref interfaceGuid,
            WlanIntfOpcode.CurrentConnection,
            IntPtr.Zero,
            out var dataSize,
            out var data,
            out _);

        if (result != ErrorSuccess || data == IntPtr.Zero)
        {
            if (data != IntPtr.Zero)
            {
                WlanFreeMemory(data);
            }

            return null;
        }

        try
        {
            if (dataSize < WlanConnectionAttributesSsidOffset + Dot11SsidBytesOffset)
            {
                return null;
            }

            var state = (WlanInterfaceState)Marshal.ReadInt32(data, 0);
            if (state != WlanInterfaceState.Connected)
            {
                return null;
            }

            var ssidLength = Marshal.ReadInt32(data, WlanConnectionAttributesSsidOffset);
            if (ssidLength <= 0 || ssidLength > 32 || dataSize < WlanConnectionAttributesSsidOffset + Dot11SsidBytesOffset + ssidLength)
            {
                return null;
            }

            var ssidBytes = new byte[ssidLength];
            Marshal.Copy(
                IntPtr.Add(data, WlanConnectionAttributesSsidOffset + Dot11SsidBytesOffset),
                ssidBytes,
                0,
                ssidBytes.Length);
            return Encoding.UTF8.GetString(ssidBytes);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    public WifiRadioState GetRadioState(WlanInterface wirelessInterface)
    {
        var interfaceGuid = wirelessInterface.InterfaceGuid;
        var result = WlanQueryInterface(
            _clientHandle,
            ref interfaceGuid,
            WlanIntfOpcode.RadioState,
            IntPtr.Zero,
            out var dataSize,
            out var data,
            out _);

        if (result != ErrorSuccess || data == IntPtr.Zero)
        {
            if (data != IntPtr.Zero)
            {
                WlanFreeMemory(data);
            }

            return new WifiRadioState(result, SoftwareRadioOff: false, HardwareRadioOff: false);
        }

        try
        {
            if (dataSize < WlanRadioStateHeaderSize + WlanPhyRadioStateSize)
            {
                return new WifiRadioState(ErrorInvalidData, SoftwareRadioOff: false, HardwareRadioOff: false);
            }

            var physicalRadioCount = Marshal.ReadInt32(data);
            var availableRadioCount = (int)((dataSize - WlanRadioStateHeaderSize) / WlanPhyRadioStateSize);
            var radioCount = Math.Min(physicalRadioCount, availableRadioCount);
            var softwareRadioOff = false;
            var hardwareRadioOff = false;
            uint? firstSoftwareOffPhyIndex = null;

            for (var index = 0; index < radioCount; index++)
            {
                var radioOffset = WlanRadioStateHeaderSize + index * WlanPhyRadioStateSize;
                var phyIndex = (uint)Marshal.ReadInt32(data, radioOffset);
                var softwareState = (Dot11RadioState)Marshal.ReadInt32(data, radioOffset + sizeof(uint));
                var hardwareState = (Dot11RadioState)Marshal.ReadInt32(data, radioOffset + sizeof(uint) + sizeof(int));

                if (softwareState == Dot11RadioState.Off)
                {
                    softwareRadioOff = true;
                    firstSoftwareOffPhyIndex ??= phyIndex;
                }

                hardwareRadioOff |= hardwareState == Dot11RadioState.Off;
            }

            return new WifiRadioState(ErrorSuccess, softwareRadioOff, hardwareRadioOff, firstSoftwareOffPhyIndex ?? 0);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    public uint EnableSoftwareRadio(WlanInterface wirelessInterface, uint phyIndex)
    {
        // WlanSetInterface expects one WLAN_PHY_RADIO_STATE, not the WLAN_RADIO_STATE returned by queries.
        var radioState = new WlanPhyRadioState
        {
            PhyIndex = phyIndex,
            SoftwareRadioState = Dot11RadioState.On,
            HardwareRadioState = Dot11RadioState.Unknown
        };
        var dataSize = (uint)Marshal.SizeOf<WlanPhyRadioState>();
        var data = Marshal.AllocHGlobal((int)dataSize);

        try
        {
            Marshal.StructureToPtr(radioState, data, fDeleteOld: false);
            var interfaceGuid = wirelessInterface.InterfaceGuid;
            return WlanSetInterface(
                _clientHandle,
                ref interfaceGuid,
                WlanIntfOpcode.RadioState,
                dataSize,
                data,
                IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    public uint BeginScan(WlanInterface wirelessInterface)
    {
        var interfaceGuid = wirelessInterface.InterfaceGuid;
        return WlanScan(_clientHandle, ref interfaceGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    public WifiScanResult ReadScanResult(WlanInterface wirelessInterface, string targetSsid, uint scanError)
    {
        var allBssResult = GetBssCount(wirelessInterface, null, out var allBssCount);
        var targetBssResult = GetBssCount(wirelessInterface, targetSsid, out var targetBssCount);

        return new WifiScanResult(scanError, allBssResult, targetBssResult, allBssCount, targetBssCount);
    }

    public void EnsureTargetProfile(WlanInterface wirelessInterface, WatchdogSettings settings)
    {
        if (!settings.ProvisionProfileOnStart)
        {
            return;
        }

        var profileExists = ProfileExists(wirelessInterface, settings.TargetProfileName);
        if (profileExists && !settings.OverwriteExistingProfileOnStart)
        {
            return;
        }

        var profileXml = BuildWpa2ProfileXml(settings);
        var interfaceGuid = wirelessInterface.InterfaceGuid;
        var result = WlanSetProfile(
            _clientHandle,
            ref interfaceGuid,
            0,
            profileXml,
            null,
            true,
            IntPtr.Zero,
            out var reasonCode);

        if (result != ErrorSuccess)
        {
            throw new WlanException("WlanSetProfile", result, reasonCode);
        }
    }

    public uint ConnectToProfile(WlanInterface wirelessInterface, string profileName)
    {
        var parameters = new WlanConnectionParameters
        {
            ConnectionMode = WlanConnectionMode.Profile,
            ProfileName = profileName,
            Dot11Ssid = IntPtr.Zero,
            DesiredBssidList = IntPtr.Zero,
            BssType = Dot11BssType.Any,
            Flags = 0
        };
        var interfaceGuid = wirelessInterface.InterfaceGuid;
        return WlanConnect(_clientHandle, ref interfaceGuid, ref parameters, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_clientHandle == IntPtr.Zero)
        {
            return;
        }

        WlanCloseHandle(_clientHandle, IntPtr.Zero);
        _clientHandle = IntPtr.Zero;
    }

    private bool ProfileExists(WlanInterface wirelessInterface, string profileName)
    {
        var interfaceGuid = wirelessInterface.InterfaceGuid;
        uint flags = 0;
        var result = WlanGetProfile(
            _clientHandle,
            ref interfaceGuid,
            profileName,
            IntPtr.Zero,
            out var profileXml,
            ref flags,
            out _);

        if (result == ErrorSuccess)
        {
            if (profileXml != IntPtr.Zero)
            {
                WlanFreeMemory(profileXml);
            }

            return true;
        }

        if (result is ErrorFileNotFound or ErrorNotFound)
        {
            if (profileXml != IntPtr.Zero)
            {
                WlanFreeMemory(profileXml);
            }

            return false;
        }

        if (profileXml != IntPtr.Zero)
        {
            WlanFreeMemory(profileXml);
        }

        throw new WlanException("WlanGetProfile", result);
    }

    private uint GetBssCount(WlanInterface wirelessInterface, string? ssid, out int bssCount)
    {
        bssCount = 0;
        var targetSsidSpecified = !string.IsNullOrEmpty(ssid);
        IntPtr ssidPointer = IntPtr.Zero;

        try
        {
            if (targetSsidSpecified)
            {
                ssidPointer = CreateDot11Ssid(ssid!);
            }

            // A specified SSID requires a concrete BSS type. This service provisions a secured WPA2 profile.
            var interfaceGuid = wirelessInterface.InterfaceGuid;
            var result = WlanGetNetworkBssList(
                _clientHandle,
                ref interfaceGuid,
                ssidPointer,
                targetSsidSpecified ? Dot11BssType.Infrastructure : Dot11BssType.Any,
                targetSsidSpecified,
                IntPtr.Zero,
                out var bssList);

            if (result != ErrorSuccess)
            {
                if (bssList != IntPtr.Zero)
                {
                    WlanFreeMemory(bssList);
                }

                return result;
            }

            try
            {
                bssCount = Marshal.ReadInt32(bssList, sizeof(uint));
                return ErrorSuccess;
            }
            finally
            {
                WlanFreeMemory(bssList);
            }
        }
        finally
        {
            if (ssidPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(ssidPointer);
            }
        }
    }

    private static IntPtr CreateDot11Ssid(string ssid)
    {
        var encodedSsid = Encoding.UTF8.GetBytes(ssid);
        if (encodedSsid.Length > 32)
        {
            throw new InvalidOperationException("SSID 的 UTF-8 长度不能超过 32 字节。");
        }

        var value = new Dot11Ssid
        {
            Length = (uint)encodedSsid.Length,
            Bytes = new byte[32]
        };
        Array.Copy(encodedSsid, value.Bytes, encodedSsid.Length);

        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Dot11Ssid>());
        Marshal.StructureToPtr(value, pointer, false);
        return pointer;
    }

    private static string BuildWpa2ProfileXml(WatchdogSettings settings)
    {
        var profileName = SecurityElement.Escape(settings.TargetProfileName) ?? string.Empty;
        var ssid = SecurityElement.Escape(settings.TargetSsid) ?? string.Empty;
        var password = SecurityElement.Escape(settings.TargetPassword) ?? string.Empty;
        var nonBroadcast = settings.ConnectToHiddenNetwork ? "true" : "false";

        return $"<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>{profileName}</name><SSIDConfig><SSID><name>{ssid}</name></SSID><nonBroadcast>{nonBroadcast}</nonBroadcast></SSIDConfig><connectionType>ESS</connectionType><connectionMode>auto</connectionMode><MSM><security><authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{password}</keyMaterial></sharedKey></security></MSM></WLANProfile>";
    }

    private static void ThrowIfFailed(uint result, string operation)
    {
        if (result != ErrorSuccess)
        {
            throw new WlanException(operation, result);
        }
    }

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanQueryInterface(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        WlanIntfOpcode opcode,
        IntPtr reserved,
        out uint dataSize,
        out IntPtr data,
        out uint valueType);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanSetInterface(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        WlanIntfOpcode opcode,
        uint dataSize,
        IntPtr data,
        IntPtr reserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanScan(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        IntPtr dot11Ssid,
        IntPtr ieData,
        IntPtr reserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanGetNetworkBssList(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        IntPtr dot11Ssid,
        Dot11BssType bssType,
        [MarshalAs(UnmanagedType.Bool)] bool securityEnabled,
        IntPtr reserved,
        out IntPtr bssList);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint WlanGetProfile(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        string profileName,
        IntPtr reserved,
        out IntPtr profileXml,
        ref uint flags,
        out uint grantedAccess);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint WlanSetProfile(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        uint flags,
        string profileXml,
        string? allUserProfileSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool overwrite,
        IntPtr reserved,
        out uint reasonCode);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint WlanConnect(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        ref WlanConnectionParameters parameters,
        IntPtr reserved);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;

        public WlanInterfaceState State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionParameters
    {
        public WlanConnectionMode ConnectionMode;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string ProfileName;

        public IntPtr Dot11Ssid;
        public IntPtr DesiredBssidList;
        public Dot11BssType BssType;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanPhyRadioState
    {
        public uint PhyIndex;
        public Dot11RadioState SoftwareRadioState;
        public Dot11RadioState HardwareRadioState;
    }
}

internal sealed record WlanInterface(Guid InterfaceGuid, string Description, WlanInterfaceState State);

internal sealed record WifiScanResult(
    uint ScanError,
    uint AllBssError,
    uint TargetBssError,
    int VisibleBssCount,
    int TargetBssCount)
{
    public bool CanSearchNetworks => ScanError == 0 && AllBssError == 0 && VisibleBssCount > 0;
}

internal sealed record WifiRadioState(
    uint QueryError,
    bool SoftwareRadioOff,
    bool HardwareRadioOff,
    uint SoftwareOffPhyIndex = 0)
{
    public bool IsKnown => QueryError == 0;
}

internal sealed class WlanException(string operation, uint errorCode, uint? reasonCode = null) : Exception(
    reasonCode is null
        ? $"{operation} 失败，Win32 错误码: {errorCode}。"
        : $"{operation} 失败，Win32 错误码: {errorCode}，WLAN 原因码: {reasonCode}。")
{
    public uint ErrorCode { get; } = errorCode;

    public uint? ReasonCode { get; } = reasonCode;
}

internal enum WlanConnectionMode
{
    Profile = 0
}

internal enum WlanInterfaceState
{
    NotReady = 0,
    Connected = 1,
    AdHocNetworkFormed = 2,
    Disconnecting = 3,
    Disconnected = 4,
    Associating = 5,
    Discovering = 6,
    Authenticating = 7
}

internal enum WlanIntfOpcode
{
    RadioState = 4,
    CurrentConnection = 7
}

internal enum Dot11RadioState
{
    Unknown = 0,
    On = 1,
    Off = 2
}

internal enum Dot11BssType
{
    Infrastructure = 1,
    Independent = 2,
    Any = 3
}
