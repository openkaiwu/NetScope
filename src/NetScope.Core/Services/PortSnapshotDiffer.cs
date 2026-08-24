using System.Collections.Immutable;
using NetScope.Core.Models;

namespace NetScope.Core.Services;

public sealed class PortSnapshotDiffer
{
    public PortDiff Compare(IEnumerable<PortBindingSnapshot> previous, IEnumerable<PortBindingSnapshot> current, DateTimeOffset now)
    {
        var before = previous.GroupBy(x => x.Key.Identity).ToDictionary(g => g.Key, g => g.First());
        var after = current.GroupBy(x => x.Key.Identity).ToDictionary(g => g.Key, g => g.First());
        var changes = ImmutableArray.CreateBuilder<PortChange>();

        foreach (var pair in after)
        {
            if (!before.TryGetValue(pair.Key, out var oldValue))
            {
                changes.Add(new(PortChangeKind.Added, null, pair.Value, now));
            }
            else if (!StringComparer.OrdinalIgnoreCase.Equals(oldValue.State, pair.Value.State))
            {
                changes.Add(new(PortChangeKind.StateChanged, oldValue, pair.Value, now));
            }
        }

        foreach (var pair in before.Where(pair => !after.ContainsKey(pair.Key)))
        {
            changes.Add(new(PortChangeKind.Removed, pair.Value, null, now));
        }

        return new(changes.ToImmutable(), after.Values.OrderBy(x => x.Port).ThenBy(x => x.Protocol).ToImmutableArray());
    }
}
