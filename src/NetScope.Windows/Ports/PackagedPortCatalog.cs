using System.Globalization;
using System.Reflection;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Ports;

public sealed class PackagedPortCatalog : IPortCatalog
{
    private readonly PortCatalogEntry[] _entries;
    private readonly Dictionary<(int Port, PortProtocol Protocol), PortCatalogEntry> _lookup;
    private readonly PortCatalogEntry[] _ranges;

    public PackagedPortCatalog()
    {
        var curated = LoadCurated().ToDictionary(x => $"{x.PortStart}/{x.Protocol?.ToString().ToLowerInvariant() ?? "any"}");
        var entries = new List<PortCatalogEntry>(12_000);
        foreach (var registered in LoadIana())
        {
            var key = $"{registered.PortStart}/{registered.Protocol?.ToString().ToLowerInvariant() ?? "any"}";
            entries.Add(curated.TryGetValue(key, out var localized)
                ? localized with { IsRegistered = true, Service = string.IsNullOrWhiteSpace(localized.Service) ? registered.Service : localized.Service }
                : registered);
        }
        foreach (var item in curated.Where(x => !entries.Any(e => e.PortStart == x.Value.PortStart && e.Protocol == x.Value.Protocol)))
            entries.Add(item.Value);
        var known = entries.Select(x => (x.PortStart, x.Protocol)).ToHashSet();
        foreach (var fallback in LoadNmap())
            if (known.Add((fallback.PortStart, fallback.Protocol))) entries.Add(fallback);
        _entries = entries.OrderBy(x => x.PortStart).ThenBy(x => x.Protocol).ToArray();
        _lookup = _entries.Where(x => x.PortStart == x.PortEnd && x.Protocol is not null)
            .GroupBy(x => (x.PortStart, x.Protocol!.Value))
            .ToDictionary(x => x.Key, x => x.OrderByDescending(e => !string.IsNullOrWhiteSpace(e.ChineseDescription)).First());
        _ranges = _entries.Where(x => x.PortStart != x.PortEnd).ToArray();
    }

    public PortCatalogEntry? Find(int port, PortProtocol protocol) =>
        _lookup.GetValueOrDefault((port, protocol)) ?? _ranges.FirstOrDefault(x => x.Contains(port, protocol));

    public IReadOnlyList<PortCatalogEntry> Search(string query, int limit = 100) =>
        _entries.Where(x => Matches(x, query))
            .OrderByDescending(x => int.TryParse(query, out var port) && x.PortStart == port && x.PortEnd == port)
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.ChineseDescription))
            .ThenBy(x => x.PortStart)
            .ThenBy(x => x.Protocol)
            .Take(limit).ToArray();

    public bool IsAssigned(int port, PortProtocol protocol) =>
        (_lookup.TryGetValue((port, protocol), out var exact) && exact.IsRegistered) || _ranges.Any(x => x.IsRegistered && x.Contains(port, protocol));

    private static IEnumerable<PortCatalogEntry> LoadIana()
    {
        using var stream = Resource("service-names-port-numbers.csv");
        using var reader = new StreamReader(stream);
        foreach (var line in ReadCsvRecords(reader).Skip(1))
        {
            var fields = ParseCsv(line);
            if (fields.Count < 4 || !TryRange(fields[1], out var start, out var end)) continue;
            var protocol = fields[2].Equals("tcp", StringComparison.OrdinalIgnoreCase) ? PortProtocol.Tcp :
                fields[2].Equals("udp", StringComparison.OrdinalIgnoreCase) ? PortProtocol.Udp : (PortProtocol?)null;
            if (protocol is null) continue;
            var service = string.IsNullOrWhiteSpace(fields[0]) ? "未命名服务" : fields[0];
            var description = fields.Count > 3 ? fields[3] : string.Empty;
            yield return new(start, end, protocol, service, description, "IANA 注册", true);
        }
    }

    private static IEnumerable<PortCatalogEntry> LoadCurated()
    {
        using var stream = Resource("common-ports.zh-CN.csv");
        using var reader = new StreamReader(stream);
        _ = reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            var fields = ParseCsv(line);
            if (fields.Count < 6 || !TryRange(fields[0], out var start, out var end)) continue;
            var protocol = fields[1].Equals("tcp", StringComparison.OrdinalIgnoreCase) ? PortProtocol.Tcp :
                fields[1].Equals("udp", StringComparison.OrdinalIgnoreCase) ? PortProtocol.Udp : (PortProtocol?)null;
            yield return new(start, end, protocol, fields[2], fields[3], fields[4], false,
                fields[5].Equals("high", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static IEnumerable<PortCatalogEntry> LoadNmap()
    {
        using var stream = Resource("nmap-services.txt");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) continue;
            var endpoint = fields[1].Split('/', 2);
            if (endpoint.Length != 2 || !int.TryParse(endpoint[0], out var port)) continue;
            var protocol = endpoint[1].Equals("tcp", StringComparison.OrdinalIgnoreCase) ? PortProtocol.Tcp :
                endpoint[1].Equals("udp", StringComparison.OrdinalIgnoreCase) ? PortProtocol.Udp : (PortProtocol?)null;
            if (protocol is null) continue;
            yield return new(port, port, protocol, fields[0], string.Empty, "基础注册表", true);
        }
    }

    private static Stream Resource(string suffix)
    {
        var assembly = typeof(PackagedPortCatalog).Assembly;
        var name = assembly.GetManifestResourceNames().Single(x => x.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"缺少资源 {suffix}");
    }

    private static bool Matches(PortCatalogEntry entry, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (int.TryParse(query.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var exactPort) && exactPort is >= 0 and <= 65_535)
            return exactPort >= entry.PortStart && exactPort <= entry.PortEnd;
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term =>
            entry.Service.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            entry.ChineseDescription.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            entry.Category.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            entry.PortStart.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (entry.PortEnd != entry.PortStart && $"{entry.PortStart}-{entry.PortEnd}".Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryRange(string value, out int start, out int end)
    {
        var parts = value.Split('-', 2);
        if (!int.TryParse(parts[0], out start)) { end = 0; return false; }
        end = parts.Length == 2 && int.TryParse(parts[1], out var parsed) ? parsed : start;
        return start is >= 0 and <= 65535 && end is >= 0 and <= 65535;
    }

    private static List<string> ParseCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        fields.Add(current.ToString());
        return fields;
    }

    private static IEnumerable<string> ReadCsvRecords(TextReader reader)
    {
        var record = new System.Text.StringBuilder();
        var quoted = false;
        while (reader.ReadLine() is { } line)
        {
            if (record.Length > 0) record.Append('\n');
            record.Append(line);
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] != '"') continue;
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
            if (quoted) continue;
            yield return record.ToString();
            record.Clear();
        }
        if (record.Length > 0) yield return record.ToString();
    }
}
