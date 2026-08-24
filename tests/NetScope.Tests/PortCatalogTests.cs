using NetScope.Core.Models;
using NetScope.Windows.Ports;

namespace NetScope.Tests;

public sealed class PortCatalogTests
{
    [Fact]
    public void LoadsOfficialIanaAndCuratedChineseDescriptions()
    {
        var catalog = new PackagedPortCatalog();

        Assert.Equal("hostname", catalog.Find(101, PortProtocol.Tcp)?.Service);
        Assert.Contains("SSH", catalog.Find(22, PortProtocol.Tcp)?.ChineseDescription ?? string.Empty);
        Assert.Contains("HTTP", catalog.Find(80, PortProtocol.Tcp)?.ChineseDescription ?? string.Empty);
    }

    [Fact]
    public void NumericSearchRanksExactPortFirstEvenWhenItIsNotOccupied()
    {
        var catalog = new PackagedPortCatalog();
        var results = catalog.Search("80", 20);

        Assert.NotEmpty(results);
        Assert.Equal(80, results[0].PortStart);
    }
}
