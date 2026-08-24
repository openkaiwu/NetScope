using NetScope.Core.Models;

namespace NetScope.Core.Services;

public sealed class PortSearchEngine
{
    public IReadOnlyList<PortBindingSnapshot> Search(IEnumerable<PortBindingSnapshot> source, string? query)
    {
        var items = source;
        query = query?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return items.OrderBy(x => x.Port).ThenBy(x => x.Protocol).ToArray();

        var split = query.Split(':', 2, StringSplitOptions.TrimEntries);
        var prefix = split.Length == 2 ? split[0].ToLowerInvariant() : string.Empty;
        var term = split.Length == 2 ? split[1] : query;
        var isNumber = int.TryParse(term, out var number);

        bool Match(PortBindingSnapshot item) => prefix switch
        {
            "port" => isNumber && item.Port == number,
            "pid" => isNumber && item.ProcessId == number,
            "proc" => Contains(item.Process?.Name, term) || Contains(item.Process?.Path, term),
            _ when isNumber => item.Port == number || item.ProcessId == number,
            _ => Contains(item.Process?.Name, term) || Contains(item.Process?.Path, term) ||
                 Contains(item.CatalogEntry?.Service, term) || Contains(item.CatalogEntry?.ChineseDescription, term) ||
                 Contains(item.LocalAddress, term) || Contains(item.State, term)
        };

        return items.Where(Match)
            .OrderByDescending(x => isNumber && x.Port == number)
            .ThenBy(x => x.Port)
            .ThenBy(x => x.Protocol)
            .ToArray();
    }

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
