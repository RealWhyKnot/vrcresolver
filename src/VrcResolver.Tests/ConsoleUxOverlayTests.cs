using VrcResolver.Shared;
using Xunit;

namespace VrcResolver.Tests;

public class ConsoleUxOverlayTests
{
    [Fact]
    public void Write_ClearsAndRestoresActiveOverlay()
    {
        TextWriter original = Console.Out;
        var writer = new StringWriter();
        var overlay = new CountingOverlay();

        try
        {
            Console.SetOut(writer);

            using IDisposable registration = ConsoleUx.UseOverlay(overlay);
            Assert.Equal(1, overlay.RenderCount);

            ConsoleUx.Write(LogComponent.Terminal, "hello");

            Assert.Equal(1, overlay.ClearCount);
            Assert.Equal(2, overlay.RenderCount);
            Assert.Contains("[terminal] hello", writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private sealed class CountingOverlay : IConsoleOverlay
    {
        public int ClearCount { get; private set; }
        public int RenderCount { get; private set; }

        public void ClearLocked()
        {
            ClearCount++;
        }

        public void RenderLocked()
        {
            RenderCount++;
        }
    }
}
