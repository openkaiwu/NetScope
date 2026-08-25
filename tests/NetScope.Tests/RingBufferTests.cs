using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class RingBufferTests
{
    [Fact]
    public void PreservesInsertionOrderUntilFull()
    {
        var buffer = new MemoryRingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        Assert.Equal(new[] { 1, 2, 3 }, buffer.Snapshot());
    }

    [Fact]
    public void OverwritesOldestWhenFull()
    {
        var buffer = new MemoryRingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4);
        buffer.Add(5);
        Assert.Equal(new[] { 3, 4, 5 }, buffer.Snapshot());
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void SnapshotIsStableCopy()
    {
        var buffer = new MemoryRingBuffer<int>(2);
        buffer.Add(1);
        var snapshot = buffer.Snapshot();
        buffer.Add(2);
        Assert.Equal(new[] { 1 }, snapshot);
        Assert.Equal(new[] { 1, 2 }, buffer.Snapshot());
    }

    [Fact]
    public void ClearEmptiesBuffer()
    {
        var buffer = new MemoryRingBuffer<int>(2);
        buffer.Add(1);
        buffer.Clear();
        Assert.Empty(buffer.Snapshot());
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryRingBuffer<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryRingBuffer<int>(-1));
    }
}
