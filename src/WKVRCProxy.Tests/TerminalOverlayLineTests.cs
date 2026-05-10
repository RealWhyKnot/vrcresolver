using System.Runtime.Versioning;
using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

[SupportedOSPlatform("windows")]
public class TerminalOverlayLineTests
{
    [Fact]
    public void Render_ReplacesPreviousFrameInsteadOfAppending()
    {
        var overlay = new TerminalOverlayLine();
        using var writer = new StringWriter();

        overlay.Render(writer, "VRChat - 0 B  WhyKnot idle  wkvrc> ");
        overlay.Render(writer, "VRChat - 0 B  WhyKnot online  wkvrc> ");

        string first = "VRChat - 0 B  WhyKnot idle  wkvrc> ";
        string second = "VRChat - 0 B  WhyKnot online  wkvrc> ";
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

        overlay.Render(writer, "wkvrc> ");
        overlay.Render(writer, "");

        Assert.Equal("wkvrc> \r       \r", writer.ToString());
        Assert.Equal(0, overlay.RenderedLength);
    }

    [Fact]
    public void RenderIfChanged_DoesNotWriteUnchangedFrame()
    {
        var overlay = new TerminalOverlayLine();
        using var writer = new StringWriter();

        Assert.True(overlay.RenderIfChanged(writer, "wkvrc> "));
        Assert.False(overlay.RenderIfChanged(writer, "wkvrc> "));

        Assert.Equal("wkvrc> ", writer.ToString());
        Assert.Equal("wkvrc> ", overlay.LastRendered);
        Assert.True(overlay.IsVisible);
    }

    [Fact]
    public void RenderIfChanged_RerendersAfterClearEvenWhenTextMatches()
    {
        var overlay = new TerminalOverlayLine();
        using var writer = new StringWriter();

        overlay.RenderIfChanged(writer, "wkvrc> ");
        overlay.Clear(writer);
        Assert.True(overlay.RenderIfChanged(writer, "wkvrc> "));

        Assert.Equal("wkvrc> \r       \rwkvrc> ", writer.ToString());
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
