using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Core.Services;

public sealed class PortRecommendationService(IPortCatalog catalog, IPortAvailabilityProbe availabilityProbe)
{
    private static readonly (int Start, int End)[] HighRiskRanges = [(1900, 1900), (4444, 4444), (5555, 5555), (6660, 7000), (31337, 31337)];

    public async ValueTask<IReadOnlyList<PortAvailabilityResult>> RecommendAsync(
        PortProtocol protocol,
        IEnumerable<PortBindingSnapshot> occupied,
        int count = 20,
        CancellationToken cancellationToken = default,
        IEnumerable<PortRange>? systemRanges = null)
    {
        var used = occupied.Where(x => x.Protocol == protocol).Select(x => x.Port).ToHashSet();
        var results = new List<PortAvailabilityResult>(count);
        var excluded = (systemRanges ?? []).Where(x => x.Protocol == protocol && x.Kind is PortRangeKind.Dynamic or PortRangeKind.Excluded).ToArray();

        for (var port = 10_000; port <= 49_151 && results.Count < count; port++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (used.Contains(port) || catalog.IsAssigned(port, protocol) || HighRiskRanges.Any(r => port >= r.Start && port <= r.End) || excluded.Any(x => x.Contains(port)))
                continue;

            var result = await availabilityProbe.ProbeAsync(port, protocol, cancellationToken);
            if (result.IsRecommended)
                results.Add(result);
        }

        return results;
    }
}
