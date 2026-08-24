using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Windows.Ports;

public sealed class WindowsPortSystemRangeProvider : IPortSystemRangeProvider
{
    private static readonly Regex ColonNumber = new(@":\s*(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ExcludedRange = new(@"^\s*(\d+)\s+(\d+)(?:\s+\*)?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public async ValueTask<SystemPortRangeSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var ranges = ImmutableArray.CreateBuilder<PortRange>();
        var usedDefault = false;

        foreach (var protocol in new[] { PortProtocol.Tcp, PortProtocol.Udp })
        {
            var name = protocol.ToString().ToLowerInvariant();
            var dynamicOutput = await RunNetshAsync($"interface ipv4 show dynamicportrange protocol={name}", cancellationToken);
            if (TryParseDynamicRange(dynamicOutput, out var start, out var end))
            {
                ranges.Add(new(start, end, protocol, PortRangeKind.Dynamic, "Windows 当前配置", $"{protocol.ToString().ToUpperInvariant()} 动态客户端端口范围"));
            }
            else
            {
                usedDefault = true;
                ranges.Add(new(49_152, 65_535, protocol, PortRangeKind.Dynamic, "Windows 默认判断", $"{protocol.ToString().ToUpperInvariant()} 默认动态客户端端口范围"));
            }

            foreach (var family in new[] { "ipv4", "ipv6" })
            {
                var excludedOutput = await RunNetshAsync($"interface {family} show excludedportrange protocol={name}", cancellationToken);
                foreach (var excluded in ParseExcludedRanges(excludedOutput, protocol, family)) ranges.Add(excluded);
            }
        }

        var distinct = ranges.DistinctBy(x => (x.Start, x.End, x.Protocol, x.Kind, x.Source)).ToImmutableArray();
        return new(distinct, usedDefault, DateTimeOffset.Now);
    }

    private static async Task<string> RunNetshAsync(string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            if (!process.Start()) return string.Empty;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeout.Token);
            return await output;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static bool TryParseDynamicRange(string output, out int start, out int end)
    {
        var values = ColonNumber.Matches(output).Select(x => int.Parse(x.Groups[1].Value)).ToArray();
        if (values.Length >= 2 && values[0] is >= 1 and <= 65_535 && values[1] > 0)
        {
            start = values[0];
            end = Math.Min(65_535, start + values[1] - 1);
            return true;
        }
        start = end = 0;
        return false;
    }

    private static IEnumerable<PortRange> ParseExcludedRanges(string output, PortProtocol protocol, string family)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ExcludedRange.Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var start) || !int.TryParse(match.Groups[2].Value, out var end)) continue;
            if (start is < 0 or > 65_535 || end < start || end > 65_535) continue;
            yield return new(start, end, protocol, PortRangeKind.Excluded, $"Windows {family.ToUpperInvariant()}", "系统排除端口范围，不应作为应用监听端口");
        }
    }

}
