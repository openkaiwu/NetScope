using NetScope.Core.Models;
using NetScope.Windows.Ports;
using System.Net;
using System.Net.Sockets;

namespace NetScope.Tests;

public sealed class PortSystemRangeTests
{
    [Fact]
    public async Task CapturesDynamicRangesForBothProtocols()
    {
        var snapshot = await new WindowsPortSystemRangeProvider().CaptureAsync();

        Assert.Contains(snapshot.Ranges, x => x.Protocol == PortProtocol.Tcp && x.Kind == PortRangeKind.Dynamic);
        Assert.Contains(snapshot.Ranges, x => x.Protocol == PortProtocol.Udp && x.Kind == PortRangeKind.Dynamic);
        Assert.All(snapshot.Ranges, x => Assert.InRange(x.Start, 0, 65_535));
        Assert.All(snapshot.Ranges, x => Assert.InRange(x.End, x.Start, 65_535));
    }

    [Fact]
    public async Task RealRecommendationsAvoidWindowsRangesAndPassExclusiveBind()
    {
        var ranges = await new WindowsPortSystemRangeProvider().CaptureAsync();
        var catalog = new PackagedPortCatalog();
        var service = new NetScope.Core.Services.PortRecommendationService(catalog, new WindowsPortAvailabilityProbe());

        var results = await service.RecommendAsync(PortProtocol.Tcp, [], 5, CancellationToken.None, ranges.Ranges);

        Assert.Equal(5, results.Count);
        Assert.All(results, item => Assert.True(item.IsRecommended));
        Assert.All(results, item => Assert.DoesNotContain(ranges.Ranges,
            range => range.Protocol == PortProtocol.Tcp && range.Kind is PortRangeKind.Dynamic or PortRangeKind.Excluded && range.Contains(item.Port)));
    }

    [Fact]
    public async Task ExclusiveBindRejectsAnAlreadyListeningTcpPortWithoutStartingAnotherListener()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var result = await new WindowsPortAvailabilityProbe().ProbeAsync(port, PortProtocol.Tcp);
            Assert.False(result.IsRecommended);
        }
        finally
        {
            listener.Stop();
        }
    }
}
