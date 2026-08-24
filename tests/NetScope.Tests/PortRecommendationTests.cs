using System.Collections.Immutable;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class PortRecommendationTests
{
    [Fact]
    public async Task ExcludesAssignedOccupiedAndHighRiskPortsAndValidatesOnlyNeededCandidates()
    {
        var catalog = new FakeCatalog([10_001]);
        var probe = new FakeProbe();
        var service = new PortRecommendationService(catalog, probe);
        var occupied = new[] { new PortBindingSnapshot(new(PortProtocol.Tcp, IpAddressFamily.IPv4, "0.0.0.0", 10_000, 1, "Listen"), DateTimeOffset.UtcNow) };
        var result = await service.RecommendAsync(PortProtocol.Tcp, occupied, 3);
        Assert.Equal([10_002, 10_003, 10_004], result.Select(x => x.Port));
        Assert.Equal(3, probe.Count);
    }

    private sealed class FakeCatalog(IEnumerable<int> assigned) : IPortCatalog
    {
        private readonly HashSet<int> _assigned = assigned.ToHashSet();
        public PortCatalogEntry? Find(int port, PortProtocol protocol) => null;
        public IReadOnlyList<PortCatalogEntry> Search(string query, int limit = 100) => [];
        public bool IsAssigned(int port, PortProtocol protocol) => _assigned.Contains(port);
    }

    private sealed class FakeProbe : IPortAvailabilityProbe
    {
        public int Count { get; private set; }
        public ValueTask<PortAvailabilityResult> ProbeAsync(int port, PortProtocol protocol, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(new PortAvailabilityResult(port, protocol, true, true, true, "ok"));
        }
    }
}
