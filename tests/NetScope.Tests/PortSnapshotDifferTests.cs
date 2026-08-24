using System.Collections.Immutable;
using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class PortSnapshotDifferTests
{
    private static PortBindingSnapshot Binding(int port, int pid, string state = "Listen", string address = "0.0.0.0", PortProtocol protocol = PortProtocol.Tcp) =>
        new(new(protocol, IpAddressFamily.IPv4, address, port, pid, state), DateTimeOffset.UtcNow);

    [Fact]
    public void DetectsAddRemoveAndStateChangesWithoutMergingAddresses()
    {
        var before = new[] { Binding(80, 1), Binding(80, 1, address: "127.0.0.1"), Binding(443, 2, "SynSent") };
        var after = new[] { Binding(80, 1), Binding(443, 2, "Established"), Binding(53, 3, "Bound", protocol: PortProtocol.Udp) };
        var diff = new PortSnapshotDiffer().Compare(before, after, DateTimeOffset.UtcNow);
        Assert.Single(diff.Changes.Where(x => x.Kind == PortChangeKind.Removed));
        Assert.Single(diff.Changes.Where(x => x.Kind == PortChangeKind.Added));
        Assert.Single(diff.Changes.Where(x => x.Kind == PortChangeKind.StateChanged));
    }
}
