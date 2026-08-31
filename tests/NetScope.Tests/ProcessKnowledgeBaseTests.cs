using NetScope.Core.Knowledge;

namespace NetScope.Tests;

public sealed class ProcessKnowledgeBaseTests
{
    [Theory]
    [InlineData("svchost.exe", "服务宿主")]
    [InlineData("SVCHOST", "服务宿主")]
    [InlineData("dwm", "桌面窗口管理器")]
    [InlineData("C:\\Windows\\System32\\MsMpEng.exe", "Windows Defender 反恶意软件引擎")]
    [InlineData("System Idle Process", "空闲进程")]
    public void TryLookup_MatchesKnownProcesses_NormalizingName(string input, string expected)
    {
        var found = ProcessKnowledgeBase.TryLookup(input, out var entry);
        Assert.True(found);
        Assert.NotNull(entry);
        Assert.Equal(expected, entry.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("chrome")]
    [InlineData("my-unknown-tool.exe")]
    public void TryLookup_ReturnsFalse_ForUnknownOrEmpty(string? input)
    {
        Assert.False(ProcessKnowledgeBase.TryLookup(input, out _));
    }

    [Fact]
    public void NormalizeKey_StripsPathAndExtension_CaseInsensitive()
    {
        Assert.Equal("svchost", ProcessKnowledgeBase.NormalizeKey("C:\\Windows\\system32\\svchost.exe"));
        Assert.Equal("explorer", ProcessKnowledgeBase.NormalizeKey("EXPLORER.EXE"));
        Assert.Equal("", ProcessKnowledgeBase.NormalizeKey(null));
    }

    [Fact]
    public void Entries_HaveNonEmptyFields_AndExecutableNameIsBareName()
    {
        Assert.True(ProcessKnowledgeBase.Count >= 30);
        foreach (var entry in ProcessKnowledgeBase.All)
        {
            Assert.Equal(entry.ExecutableName, Path.GetFileName(entry.ExecutableName));
            Assert.DoesNotContain(".exe", entry.ExecutableName, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(entry.Category));
            Assert.False(string.IsNullOrWhiteSpace(entry.Purpose));
            Assert.False(string.IsNullOrWhiteSpace(entry.TerminationAdvice));
        }
    }

    [Fact]
    public void TryLookup_AllEntriesAreRetrievable_ByTheirOwnExecutableName()
    {
        foreach (var entry in ProcessKnowledgeBase.All)
            Assert.True(ProcessKnowledgeBase.TryLookup(entry.ExecutableName, out var hit) && hit == entry);
    }
}
