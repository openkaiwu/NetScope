using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class NetworkPerformanceMathTests
{
    [Fact]
    public void CalculatesInterpolatedPercentileAndConsecutiveJitter()
    {
        double[] samples = [1, 3, 6, 10];

        Assert.Equal(9.4, NetworkPerformanceMath.Percentile(samples, .95), 3);
        Assert.Equal(3, NetworkPerformanceMath.AverageJitter(samples), 3);
    }

    [Fact]
    public void ConvertsTransferredBytesToDecimalMegabitsPerSecond()
    {
        var speed = NetworkPerformanceMath.ToMegabitsPerSecond(10_000_000, TimeSpan.FromSeconds(2));

        Assert.Equal(40, speed, 3);
    }

    [Theory]
    [InlineData(4, "A+")]
    [InlineData(14, "A")]
    [InlineData(29, "B")]
    [InlineData(59, "C")]
    [InlineData(119, "D")]
    [InlineData(120, "F")]
    public void GradesBufferbloatFromLoadedLatencyDelta(double delta, string expected)
    {
        Assert.Equal(expected, NetworkPerformanceMath.BufferbloatGrade(delta));
    }

    [Fact]
    public void FormatsSubMillisecondLatencyWithoutReportingZero()
    {
        Assert.Equal("<1 ms", NetworkPerformanceMath.FormatLatency(.42));
    }
}
