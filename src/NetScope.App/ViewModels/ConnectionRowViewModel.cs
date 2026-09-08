using NetScope.Core.Models;

namespace NetScope.App.ViewModels;

public sealed class ConnectionRowViewModel(TcpConnectionRecord record)
{
    public string Process => $"{record.ProcessName} ({record.ProcessId})";
    public string Remote => $"[{record.RemoteAddress}]:{record.RemotePort}";
    public string RemoteAddress => record.RemoteAddress;
    public string Local => $"[{record.LocalAddress}]:{record.LocalPort}";
    public string RemotePort => record.RemotePort.ToString();
    public string State => record.EndedAt is null
        ? DateTimeOffset.Now - record.LastSeenAt > TimeSpan.FromSeconds(10) ? $"{record.State} · 待更新" : record.State
        : $"{record.State} · 已停止观察";
    public string FirstSeen => record.FirstSeenAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string LastSeen => record.LastSeenAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string End => record.EndedAt?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "观察中";
    public string Detail => $"{record.AddressFamily}；进程启动：{record.ProcessStartedAt?.ToLocalTime().ToString("g") ?? "未知"}；{record.EndReason ?? "当前快照可见"}";
}
