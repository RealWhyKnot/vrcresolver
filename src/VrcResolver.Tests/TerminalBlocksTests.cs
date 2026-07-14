using VrcResolver;
using Xunit;

namespace VrcResolver.Tests;

public class TerminalBlocksTests
{
    [Fact]
    public void Panel_ColorsReconnectingAsWarning()
    {
        IReadOnlyList<TerminalFrame> frames = TerminalBlocks.Panel(
            "status",
            new[] { ("mesh", "reconnecting", "waiting for backend") },
            width: 80,
            glyphs: TerminalGlyphSet.Ascii);

        TerminalTextRun state = frames
            .SelectMany(static frame => frame.Runs)
            .First(run => run.Text.Contains("reconnecting", StringComparison.Ordinal));

        Assert.Equal(ConsoleColor.Yellow, state.Color);
    }

    [Fact]
    public void Panel_DoesNotTreatBlockedAsHealthy()
    {
        IReadOnlyList<TerminalFrame> frames = TerminalBlocks.Panel(
            "diagnostics",
            new[]
            {
                ("mesh", "online", "connected to resolver"),
                ("relay", "missing", "local port unavailable"),
            },
            width: 80,
            glyphs: TerminalGlyphSet.Ascii);

        TerminalTextRun online = frames
            .SelectMany(static frame => frame.Runs)
            .First(run => run.Text.Contains("online", StringComparison.Ordinal));
        TerminalTextRun missing = frames
            .SelectMany(static frame => frame.Runs)
            .First(run => run.Text.Contains("missing", StringComparison.Ordinal));

        Assert.Equal(ConsoleColor.Green, online.Color);
        Assert.Equal(ConsoleColor.Red, missing.Color);
    }
}
