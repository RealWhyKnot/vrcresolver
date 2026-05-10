using System.Runtime.Versioning;
using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

[SupportedOSPlatform("windows")]
public class TerminalInputBufferTests
{
    [Fact]
    public void HistoryNavigation_WalksBackwardAndForward()
    {
        var buffer = new TerminalInputBuffer(new[] { "settings", "status" });

        buffer.PreviousHistory();
        Assert.Equal("status", buffer.Text());

        buffer.PreviousHistory();
        Assert.Equal("settings", buffer.Text());

        buffer.NextHistory();
        Assert.Equal("status", buffer.Text());

        buffer.NextHistory();
        Assert.Equal("", buffer.Text());
    }

    [Fact]
    public void Remember_DeduplicatesConsecutiveCommands()
    {
        var buffer = new TerminalInputBuffer();

        buffer.Remember("status");
        buffer.Remember("status");
        buffer.PreviousHistory();

        Assert.Equal("status", buffer.Text());
        buffer.PreviousHistory();
        Assert.Equal("status", buffer.Text());
    }

    [Fact]
    public void TabCompletion_ReturnsSingleCommandReplacement()
    {
        var registry = TerminalCommandRegistry.CreateDefault();

        TerminalCompletion completion = registry.Complete("/sta");

        Assert.Equal("/status", completion.Replacement);
        Assert.Empty(completion.Suggestions);
    }

    [Fact]
    public void SlashCompletion_ReturnsAllCommandsForBareSlash()
    {
        var registry = TerminalCommandRegistry.CreateDefault();

        TerminalCompletion completion = registry.Complete("/");

        Assert.Equal("", completion.Replacement);
        Assert.Contains(completion.Suggestions, c => c.Name == "settings");
        Assert.Contains(completion.Suggestions, c => c.Name == "status");
    }

    [Fact]
    public void SlashCompletion_AutofillsUniquePrefix()
    {
        var registry = TerminalCommandRegistry.CreateDefault();

        TerminalCompletion completion = registry.Complete("/se");

        Assert.Equal("/settings", completion.Replacement);
        Assert.Empty(completion.Suggestions);
    }

    [Fact]
    public void SlashCompletion_ReturnsSuggestionsForAmbiguousPrefix()
    {
        var registry = TerminalCommandRegistry.CreateDefault();

        TerminalCompletion completion = registry.Complete("/s");

        Assert.Equal("", completion.Replacement);
        Assert.Contains(completion.Suggestions, c => c.Name == "settings");
        Assert.Contains(completion.Suggestions, c => c.Name == "status");
    }

    [Fact]
    public void TabCompletion_AcceptsCommandAliases()
    {
        var registry = TerminalCommandRegistry.CreateDefault();

        TerminalCompletion completion = registry.Complete("/act");

        Assert.Equal("/status", completion.Replacement);
        Assert.Empty(completion.Suggestions);
    }
}
