using System.Collections.Immutable;
using System.Net;
using System.Runtime.InteropServices;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Ports;

public sealed class WindowsPortTableProvider : IPortTableProvider
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint ErrorInsufficientBuffer = 122;

    public ValueTask<ImmutableArray<PortBindingSnapshot>> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        var rows = ImmutableArray.CreateBuilder<PortBindingSnapshot>();
        ReadTcpTable<MibTcpRowOwnerPid>(AfInet, IpAddressFamily.IPv4, 5, row =>
            AddTcp4(rows, row, now));
        ReadTcpTable<MibTcp6RowOwnerPid>(AfInet6, IpAddressFamily.IPv6, 5, row =>
            AddTcp6(rows, row, now));
        ReadUdpTable<MibUdpRowOwnerPid>(AfInet, 1, row => AddUdp4(rows, row, now));
        ReadUdpTable<MibUdp6RowOwnerPid>(AfInet6, 1, row => AddUdp6(rows, row, now));
        return ValueTask.FromResult(rows.ToImmutable());
    }

    private static void ReadTcpTable<TRow>(int family, IpAddressFamily addressFamily, int tableClass, Action<TRow> add) where TRow : struct
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, tableClass, 0);
        if (result != ErrorInsufficientBuffer || size <= sizeof(uint)) return;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, family, tableClass, 0) != 0) return;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TRow>();
            var pointer = IntPtr.Add(buffer, sizeof(uint));
            for (var index = 0; index < count; index++)
                add(Marshal.PtrToStructure<TRow>(IntPtr.Add(pointer, index * rowSize)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ReadUdpTable<TRow>(int family, int tableClass, Action<TRow> add) where TRow : struct
    {
        var size = 0;
        var result = GetExtendedUdpTable(IntPtr.Zero, ref size, true, family, tableClass, 0);
        if (result != ErrorInsufficientBuffer || size <= sizeof(uint)) return;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, true, family, tableClass, 0) != 0) return;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TRow>();
            var pointer = IntPtr.Add(buffer, sizeof(uint));
            for (var index = 0; index < count; index++)
                add(Marshal.PtrToStructure<TRow>(IntPtr.Add(pointer, index * rowSize)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void AddTcp4(ImmutableArray<PortBindingSnapshot>.Builder rows, MibTcpRowOwnerPid row, DateTimeOffset now) =>
        rows.Add(Create(PortProtocol.Tcp, IpAddressFamily.IPv4, new IPAddress(row.LocalAddr).ToString(),
            DecodePort(row.LocalPort), unchecked((int)row.OwningPid), TcpState(row.State), now));

    private static void AddTcp6(ImmutableArray<PortBindingSnapshot>.Builder rows, MibTcp6RowOwnerPid row, DateTimeOffset now) =>
        rows.Add(Create(PortProtocol.Tcp, IpAddressFamily.IPv6, new IPAddress(row.LocalAddr, row.LocalScopeId).ToString(),
            DecodePort(row.LocalPort), unchecked((int)row.OwningPid), TcpState(row.State), now));

    private static void AddUdp4(ImmutableArray<PortBindingSnapshot>.Builder rows, MibUdpRowOwnerPid row, DateTimeOffset now) =>
        rows.Add(Create(PortProtocol.Udp, IpAddressFamily.IPv4, new IPAddress(row.LocalAddr).ToString(),
            DecodePort(row.LocalPort), unchecked((int)row.OwningPid), "Bound", now));

    private static void AddUdp6(ImmutableArray<PortBindingSnapshot>.Builder rows, MibUdp6RowOwnerPid row, DateTimeOffset now) =>
        rows.Add(Create(PortProtocol.Udp, IpAddressFamily.IPv6, new IPAddress(row.LocalAddr, row.LocalScopeId).ToString(),
            DecodePort(row.LocalPort), unchecked((int)row.OwningPid), "Bound", now));

    private static PortBindingSnapshot Create(PortProtocol protocol, IpAddressFamily family, string address, int port, int pid, string state, DateTimeOffset now) =>
        new(new(protocol, family, address, port, pid, state), now);

    private static int DecodePort(uint value) => (int)((value & 0xFF) << 8 | (value >> 8) & 0xFF);

    private static string TcpState(uint state) => state switch
    {
        1 => "Closed", 2 => "Listen", 3 => "SynSent", 4 => "SynReceived", 5 => "Established",
        6 => "FinWait1", 7 => "FinWait2", 8 => "CloseWait", 9 => "Closing", 10 => "LastAck",
        11 => "TimeWait", 12 => "DeleteTcb", _ => "Unknown"
    };

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref int size, bool order, int ipVersion, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr udpTable, ref int size, bool order, int ipVersion, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MibTcpRowOwnerPid
    {
        public readonly uint State;
        public readonly uint LocalAddr;
        public readonly uint LocalPort;
        public readonly uint RemoteAddr;
        public readonly uint RemotePort;
        public readonly uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MibUdpRowOwnerPid
    {
        public readonly uint LocalAddr;
        public readonly uint LocalPort;
        public readonly uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }
}
