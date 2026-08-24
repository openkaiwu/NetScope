using NetScope.Core.Models;
using NetScope.Windows.Network;

namespace NetScope.Tests;

public sealed class NetworkPerformanceTesterTests
{
    [Fact]
    public async Task ReturnsCancelledResultWithoutStartingTrafficWhenTokenIsAlreadyCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new HttpNetworkPerformanceTester().RunAsync(
            NetworkPerformanceTestOptions.CloudflareDefault, cancellationToken: cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.WasCancelled);
        Assert.Equal(0, result.DownloadBytes + result.UploadBytes);
    }

    [Fact]
    public async Task RejectsNonHttpsSpeedEndpoints()
    {
        var options = NetworkPerformanceTestOptions.CloudflareDefault with
        {
            DownloadEndpoint = new Uri("http://example.test/down")
        };

        var result = await new HttpNetworkPerformanceTester().RunAsync(options);

        Assert.False(result.Succeeded);
        Assert.Contains("HTTPS", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
