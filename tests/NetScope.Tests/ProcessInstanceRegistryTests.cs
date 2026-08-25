using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class ProcessInstanceRegistryTests
{
    [Fact]
    public void SamePidDifferentStartTimeIsNewInstance()
    {
        var registry = new ProcessInstanceRegistry();
        var first = registry.Register(100, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "app");
        var second = registry.Register(100, DateTimeOffset.Parse("2026-01-01T00:05:00Z"), "app");

        Assert.NotEqual(first, second);
        Assert.Equal(second, registry.TryGetCurrentKey(100));
        Assert.False(registry.TryGetName(first, out _));
        Assert.True(registry.TryGetName(second, out var name));
        Assert.Equal("app", name);
    }

    [Fact]
    public void SamePidSameStartTimeIsSameInstance()
    {
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var registry = new ProcessInstanceRegistry();
        var first = registry.Register(100, started, "app");
        var second = registry.Register(100, started, "app");
        Assert.Equal(first, second);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void DistinctPidsRemainDistinct()
    {
        var registry = new ProcessInstanceRegistry();
        registry.Register(1, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "a");
        registry.Register(2, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "b");
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void RemoveExitedDropsStaleInstances()
    {
        var registry = new ProcessInstanceRegistry();
        var live = registry.Register(1, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "a");
        registry.Register(2, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "b");

        registry.RemoveExited([live]);

        Assert.Equal(1, registry.Count);
        Assert.Equal(live, registry.TryGetCurrentKey(1));
        Assert.Null(registry.TryGetCurrentKey(2));
    }
}
