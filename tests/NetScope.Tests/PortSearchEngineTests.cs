using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class PortSearchEngineTests
{
    private static PortBindingSnapshot Binding(int port, int pid, string name, string purpose) =>
        new(new(PortProtocol.Tcp, IpAddressFamily.IPv4, "0.0.0.0", port, pid, "Listen"), DateTimeOffset.UtcNow,
            new(pid, DateTimeOffset.UtcNow, name, $@"C:\Apps\{name}.exe", true, false),
            new(port, port, PortProtocol.Tcp, name, purpose, "测试", true));

    [Theory]
    [InlineData("port:443", 443)]
    [InlineData("pid:80", 443)]
    [InlineData("proc:nginx", 443)]
    [InlineData("网页", 443)]
    public void SupportsPrefixesAndLocalizedPurpose(string query, int expectedPort)
    {
        var data = new[] { Binding(443, 80, "nginx", "加密网页通信"), Binding(8080, 80, "dotnet", "开发服务") };
        var result = new PortSearchEngine().Search(data, query);
        Assert.Equal(expectedPort, result[0].Port);
    }

    [Fact]
    public void ExactPortWinsForNumericQuery()
    {
        var data = new[] { Binding(80, 999, "http", "网页"), Binding(443, 80, "nginx", "网页") };
        Assert.Equal(80, new PortSearchEngine().Search(data, "80")[0].Port);
    }

    [Fact]
    public void SearchesFiveThousandRowsWithinTargetBudget()
    {
        var data = Enumerable.Range(10_000, 5_000).Select((port, index) => Binding(port, index + 100, $"worker-{index}", "开发服务")).ToArray();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new PortSearchEngine().Search(data, "proc:worker-4321");
        stopwatch.Stop();
        Assert.Single(result);
        Assert.True(stopwatch.ElapsedMilliseconds < 50, $"Search took {stopwatch.ElapsedMilliseconds}ms");
    }
}
