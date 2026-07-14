using System.Runtime.Versioning;
using VrcResolver;
using Xunit;

namespace VrcResolver.Tests;

[SupportedOSPlatform("windows")]
public class TerminalOverlayLineTests
{
    [Fact]
    public void Render_ReplacesPreviousFrameInsteadOfAppending()
    {
        var overlay = new TerminalOverlayLine();
        using var writer = new StringWriter();

        overlay.Render(writer, "VRChat - 0 B  resolver idle  vrcr> ");
        overlay.Render(writer, "VRChat - 0 B  resolver online  vrcr> ");

        string first = "VRChat - 0 B  resolver idle  vrcr> ";
        string second = "VRChat - 0 B  resolver online  vrcr> ";
        Assert.Equal(first + "\r" + new string(' ', first.Length) + "\r" + second, writer.ToString());
        Assert.Equal(second.Length, overlay.RenderedLength);
    }

    [Fact]
    public void Clear_IsNoOpBeforeFirstRender()
    {
        var overlay = new TerminalOverlayLine();
        using var writer = new StringWriter();

        overlay.Clear(writer);

        Assert.Equal("", writer.ToString());
        Assert.Equal(0, overlay.RenderedLength);
    }

    [Fact]
    public void EmptyRender_ClearsPreviousFrame()
    {
        var overlay = new TerminalOverlayLine();
        using var writer = new StringWriter();

        overlay.Render(writer, "vrcr> ");
        overlay.Render(writer, "");

        Assert.Equal("vrcr> \r      \r", writer.ToString());
        Assert.Equal(0, overlay.RenderedLength);
    }

    [Fact]
    public void RenderIfChanged_DoesNotWriteUnchangedFrame()
    {
        var overlay = new TerminalOverlayLine();
        using var writer = new StringWriter();

        Assert.True(overlay.RenderIfChanged(writer, "vrcr> "));
        Assert.False(overlay.RenderIfChanged(writer, "vrcr> "));

        Assert.Equal("vrcr> ", writer.ToString());
        Assert.Equal("vrcr> ", overlay.LastRendered);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public void RenderIfChanged_RerendersAfterClearEvenWhenTextMatches()
    {
        var overlay = new TerminalOverlayLine();
        using var writer = new StringWriter();

        overlay.RenderIfChanged(writer, "vrcr> ");
        overlay.Clear(writer);
        Assert.True(overlay.RenderIfChanged(writer, "vrcr> "));

        Assert.Equal("vrcr> \r      \rvrcr> ", writer.ToString());
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public void RenderIfChanged_WritesStyledFrameOnceForUnchangedPlainText()
    {
        var overlay = new TerminalOverlayLine();
        using var writer = new StringWriter();
        var frame = TerminalFrame.FromRuns(
            new TerminalTextRun("VRChat ", ConsoleColor.Gray),
            new TerminalTextRun("serving", ConsoleColor.Green));
        int writes = 0;

        Assert.True(overlay.RenderIfChanged(writer, frame, (w, runs) =>
        {
            writes++;
            foreach (TerminalTextRun run in runs)
                w.Write(run.Text);
        }));
        Assert.False(overlay.RenderIfChanged(writer, frame, (w, runs) =>
        {
            writes++;
            foreach (TerminalTextRun run in runs)
                w.Write(run.Text);
        }));

        Assert.Equal("VRChat serving", writer.ToString());
        Assert.Equal(1, writes);
        Assert.Equal("VRChat serving", overlay.LastRendered);
    }
}
