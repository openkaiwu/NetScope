using System.Runtime.InteropServices;
using System.Text;

namespace NetScope.Windows.Network;

internal sealed class NativeWifiSignalProvider
{
    public WifiConnectionInfo? TryGetConnectionInfo(string interfaceId)
    {
        if (!Guid.TryParse(interfaceId, out var targetId)) return null;
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var client) != 0) return null;
        try
        {
            if (WlanEnumInterfaces(client, IntPtr.Zero, out var listPointer) != 0) return null;
            try
            {
                var count = Marshal.ReadInt32(listPointer);
                var offset = 8;
                var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WlanInterfaceInfo>(IntPtr.Add(listPointer, offset + i * itemSize));
                    if (info.InterfaceGuid != targetId) continue;
                    var id = info.InterfaceGuid;
                    if (WlanQueryInterface(client, ref id, 7, IntPtr.Zero, out _, out var data, out _) != 0) return null;
                    try
                    {
                        var attributes = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
                        var ssidBytes = attributes.Association.Ssid.Value ?? [];
                        var ssidLength = (int)Math.Min(attributes.Association.Ssid.Length, (uint)ssidBytes.Length);
                        var ssid = ssidLength == 0 ? null : Encoding.UTF8.GetString(ssidBytes, 0, ssidLength);
                        return new WifiConnectionInfo(
                            (int)Math.Clamp(attributes.Association.SignalQuality, 0u, 100u),
                            ssid,
                            attributes.Association.RxRate * 1000L,
                            attributes.Association.TxRate * 1000L);
                    }
                    finally { WlanFreeMemory(data); }
                }
                return null;
            }
            finally { WlanFreeMemory(listPointer); }
        }
        finally { WlanCloseHandle(client, IntPtr.Zero); }
    }

    public int? TryGetSignalQuality(string interfaceId) => TryGetConnectionInfo(interfaceId)?.SignalQuality;

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opcode, IntPtr reserved,
        out uint dataSize, out IntPtr data, out int valueType);
    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public int State;
        public int ConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public WlanAssociationAttributes Association;
        public WlanSecurityAttributes Security;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociationAttributes
    {
        public Dot11Ssid Ssid;
        public int BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public int PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint RxRate;
        public uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanSecurityAttributes
    {
        [MarshalAs(UnmanagedType.Bool)] public bool SecurityEnabled;
        [MarshalAs(UnmanagedType.Bool)] public bool OneXEnabled;
        public int AuthAlgorithm;
        public int CipherAlgorithm;
    }
}

internal sealed record WifiConnectionInfo(int SignalQuality, string? Ssid, long ReceiveRateBitsPerSecond, long TransmitRateBitsPerSecond);
