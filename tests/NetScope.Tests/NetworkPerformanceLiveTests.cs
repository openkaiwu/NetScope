using NetScope.Core.Models;
using NetScope.Windows.Network;

namespace NetScope.Tests;

public sealed class NetworkPerformanceLiveTests
{
    [Fact]
    public async Task CloudflareDownloadUploadPathWorksWhenLiveTestIsEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("NETSCOPE_LIVE_NETWORK_TEST"), "1", StringComparison.Ordinal))
            return;

        var options = new NetworkPerformanceTestOptions(
            new Uri("https://speed.cloudflare.com/__down"),
            new Uri("https://speed.cloudflare.com/__up"),
            IdleLatencySamples: 3,
            DownloadWarmupBytes: 50_000,
            DownloadMinimumBytes: 100_000,
            DownloadMaximumBytes: 100_000,
            UploadWarmupBytes: 32_000,
            UploadMinimumBytes: 64_000,
            UploadMaximumBytes: 64_000);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new HttpNetworkPerformanceTester().RunAsync(options, cancellationToken: timeout.Token);

        Assert.True(result.Succeeded, result.Error ?? result.Summary);
        Assert.True(result.DownloadMegabitsPerSecond > 0);
        Assert.True(result.UploadMegabitsPerSecond > 0);
        Assert.True(result.IdleLatencySamples.Length >= 3);
        Assert.InRange(result.DownloadBytes + result.UploadBytes, 246_000, options.MaximumTransferBytes);
    }
}
