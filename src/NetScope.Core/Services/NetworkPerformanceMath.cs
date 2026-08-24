namespace NetScope.Core.Services;

public static class NetworkPerformanceMath
{
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (ordered.Length == 0) return 0;
        var rank = Math.Clamp(percentile, 0, 1) * (ordered.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return ordered[lower];
        return ordered[lower] + (ordered[upper] - ordered[lower]) * (rank - lower);
    }

    public static double AverageJitter(IEnumerable<double> values)
    {
        var samples = values.Where(double.IsFinite).ToArray();
        if (samples.Length < 2) return 0;
        return samples.Zip(samples.Skip(1), (left, right) => Math.Abs(right - left)).Average();
    }

    public static double ToMegabitsPerSecond(long bytes, TimeSpan duration) =>
        bytes <= 0 || duration <= TimeSpan.Zero ? 0 : bytes * 8d / duration.TotalSeconds / 1_000_000d;

    public static string BufferbloatGrade(double deltaMilliseconds) => deltaMilliseconds switch
    {
        < 5 => "A+",
        < 15 => "A",
        < 30 => "B",
        < 60 => "C",
        < 120 => "D",
        _ => "F"
    };

    public static string FormatLatency(double milliseconds) => milliseconds switch
    {
        <= 0 => "—",
        < 1 => "<1 ms",
        _ => $"{milliseconds:0.#} ms"
    };
}
