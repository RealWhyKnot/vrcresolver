using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using WKVRCProxy.Shared;

namespace WKVRCProxy;

[SupportedOSPlatform("windows")]
internal sealed class InteractiveTerminal : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(125);
    internal static readonly TimeSpan ActivityWindow = TimeSpan.FromSeconds(2);

    private readonly Action _requestShutdown;
    private readonly Func<bool> _meshConnected;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _input = new();
    private readonly object _inputLock = new();
    private readonly Overlay _overlay;

    private IDisposable? _overlayRegistration;
    private Task? _inputTask;
    private Task? _renderTask;
    private int _spinnerIndex;
    private int _started;
    private volatile bool _stopped;

    public InteractiveTerminal(Action requestShutdown, Func<bool> meshConnected)
    {
        _requestShutdown = requestShutdown ?? throw new ArgumentNullException(nameof(requestShutdown));
        _meshConnected = meshConnected ?? throw new ArgumentNullException(nameof(meshConnected));
        _overlay = new Overlay(this);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        if (!CanUseInteractiveConsole()) return;

        _overlayRegistration = ConsoleUx.UseOverlay(_overlay);
        ConsoleUx.Write(LogComponent.Terminal, "interactive terminal ready; type 'help' for commands.");
        _inputTask = Task.Run(() => InputLoopAsync(_cts.Token));
        _renderTask = Task.Run(() => RenderLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _stopped = true;
        _cts.Cancel();
        _overlayRegistration?.Dispose();
        await AwaitNoThrowAsync(_renderTask, 500).ConfigureAwait(false);
        await AwaitNoThrowAsync(_inputTask, 500).ConfigureAwait(false);
    }

    private static bool CanUseInteractiveConsole()
    {
        if (!Environment.UserInteractive) return false;
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;
        string? disabled = Environment.GetEnvironmentVariable("WKVRCPROXY_NO_INTERACTIVE_TERMINAL");
        return !string.Equals(disabled, "1", StringComparison.Ordinal)
            && !string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase);
    }

    private async Task InputLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                    continue;
                }

                HandleKey(Console.ReadKey(intercept: true));
            }
            catch (OperationCanceledException) { return; }
            catch (InvalidOperationException) { return; }
            catch (IOException) { return; }
            catch (Exception ex)
            {
                ConsoleUx.Warn(LogComponent.Terminal, "input disabled: " + ex.GetType().Name + ": " + ex.Message);
                return;
            }
        }
    }

    private async Task RenderLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RefreshInterval, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _spinnerIndex);
                ConsoleUx.WithConsoleLock(_overlay.RenderLocked);
            }
            catch (OperationCanceledException) { return; }
            catch { return; }
        }
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            if (key.Key == ConsoleKey.U)
            {
                SetInput("");
                Redraw();
            }
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                SubmitInput();
                return;
            case ConsoleKey.Backspace:
                RemoveLastInputChar();
                Redraw();
                return;
            case ConsoleKey.Escape:
                SetInput("");
                Redraw();
                return;
            case ConsoleKey.Tab:
                CompleteInput();
                Redraw();
                return;
        }

        if (!char.IsControl(key.KeyChar))
        {
            AppendInput(key.KeyChar);
            Redraw();
        }
    }

    private void SubmitInput()
    {
        string command = TakeInput().Trim();
        if (command.Length == 0)
        {
            Redraw();
            return;
        }

        ConsoleUx.WithConsoleLock(() =>
        {
            _overlay.ClearLocked();
            WriteColored(ConsoleColor.White, "wkvrc> ");
            WriteColored(ConsoleColor.Gray, command);
            Console.WriteLine();
            _overlay.RenderLocked();
        });

        ExecuteCommand(command);
    }

    private void ExecuteCommand(string command)
    {
        string[] parts = command.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length == 0 ? "" : parts[0].ToLowerInvariant();
        string args = parts.Length == 2 ? parts[1] : "";

        switch (verb)
        {
            case "help":
                ConsoleUx.Write(LogComponent.Terminal, "commands: settings, help, clear, quit");
                break;
            case "settings":
                if (args.Length > 0)
                    ConsoleUx.Warn(LogComponent.Terminal, "settings has no editable values in this build.");
                else
                    ConsoleUx.Write(LogComponent.Terminal, "settings: no editable values in this build.");
                break;
            case "clear":
                ClearConsole();
                break;
            case "quit":
            case "exit":
                ConsoleUx.Write(LogComponent.Terminal, "quit requested; shutting down.");
                _requestShutdown();
                break;
            default:
                ConsoleUx.Warn(LogComponent.Terminal, "unknown command: " + command + " (type 'help')");
                break;
        }
    }

    private void CompleteInput()
    {
        string current = GetInput();
        if ("settings".StartsWith(current, StringComparison.OrdinalIgnoreCase))
            SetInput("settings");
        else if ("help".StartsWith(current, StringComparison.OrdinalIgnoreCase))
            SetInput("help");
        else if ("quit".StartsWith(current, StringComparison.OrdinalIgnoreCase))
            SetInput("quit");
    }

    private void ClearConsole()
    {
        ConsoleUx.WithConsoleLock(() =>
        {
            _overlay.ClearLocked();
            try { Console.Clear(); }
            catch { /* non-clearable host */ }
            _overlay.RenderLocked();
        });
    }

    private void Redraw()
    {
        if (_stopped) return;
        ConsoleUx.WithConsoleLock(_overlay.RenderLocked);
    }

    private void AppendInput(char ch)
    {
        lock (_inputLock)
        {
            if (_input.Length < 512)
                _input.Append(ch);
        }
    }

    private void RemoveLastInputChar()
    {
        lock (_inputLock)
        {
            if (_input.Length > 0)
                _input.Length--;
        }
    }

    private string TakeInput()
    {
        lock (_inputLock)
        {
            string value = _input.ToString();
            _input.Clear();
            return value;
        }
    }

    private string GetInput()
    {
        lock (_inputLock) return _input.ToString();
    }

    private void SetInput(string value)
    {
        lock (_inputLock)
        {
            _input.Clear();
            _input.Append(value);
        }
    }

    private WatchdogActivitySnapshot Snapshot() => WatchdogStats.GetActivitySnapshot();

    private bool MeshConnected()
    {
        try { return _meshConnected(); }
        catch { return false; }
    }

    private int SpinnerIndex() => Volatile.Read(ref _spinnerIndex);

    private static async Task AwaitNoThrowAsync(Task? task, int timeoutMs)
    {
        if (task == null) return;
        try
        {
            Task timeout = Task.Delay(timeoutMs);
            await Task.WhenAny(task, timeout).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    private static int ConsoleWidth()
    {
        try { return Math.Max(20, Console.WindowWidth - 1); }
        catch { return 119; }
    }

    private static void WriteColored(ConsoleColor color, string text)
    {
        ConsoleColor prev;
        try { prev = Console.ForegroundColor; }
        catch { prev = ConsoleColor.Gray; }
        try
        {
            try { Console.ForegroundColor = color; } catch { /* no-tty */ }
            Console.Write(text);
        }
        finally
        {
            try { Console.ForegroundColor = prev; } catch { /* no-tty */ }
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _cts.Cancel();
        _overlayRegistration?.Dispose();
        _cts.Dispose();
    }

    private sealed class Overlay : IConsoleOverlay
    {
        private readonly InteractiveTerminal _owner;
        private int _renderedLength;

        public Overlay(InteractiveTerminal owner)
        {
            _owner = owner;
        }

        public void ClearLocked()
        {
            if (_renderedLength <= 0) return;
            Console.Write('\r');
            Console.Write(new string(' ', _renderedLength));
            Console.Write('\r');
            _renderedLength = 0;
        }

        public void RenderLocked()
        {
            if (_owner._stopped) return;
            ClearLocked();
            string line = TerminalStatusFormatter.FormatLine(
                _owner.Snapshot(),
                DateTime.UtcNow,
                _owner.MeshConnected(),
                _owner.SpinnerIndex(),
                ConsoleWidth(),
                _owner.GetInput());
            WriteColored(ConsoleColor.DarkGray, line);
            _renderedLength = line.Length;
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class TerminalStatusFormatter
{
    private static readonly char[] s_frames = { '|', '/', '-', '\\' };

    public static string FormatLine(
        WatchdogActivitySnapshot snapshot,
        DateTime nowUtc,
        bool meshConnected,
        int spinnerIndex,
        int width,
        string input)
    {
        width = NormalizeWidth(width);
        char frame = s_frames[(spinnerIndex & int.MaxValue) % s_frames.Length];
        char relayFrame = snapshot.RelayActive(nowUtc, InteractiveTerminal.ActivityWindow) ? frame : '-';
        char whyKnotFrame = snapshot.WhyKnotActive(nowUtc, InteractiveTerminal.ActivityWindow) ? frame : '-';

        string status = string.Format(
            CultureInfo.InvariantCulture,
            "lh-yt {0} {1} | whyknot {2} {3} | {4}",
            relayFrame,
            Heartbeat.FormatBytes(snapshot.RelayBytesTotal),
            whyKnotFrame,
            Heartbeat.FormatBytes(snapshot.WhyKnotRelayBytesTotal),
            meshConnected ? "mesh up" : "mesh down");
        return Fit(status, "wkvrc> ", input ?? "", width);
    }

    private static int NormalizeWidth(int width)
    {
        if (width <= 0) return 119;
        return Math.Clamp(width, 20, 180);
    }

    private static string Fit(string status, string prompt, string input, int width)
    {
        int inputBudget = Math.Max(0, width - status.Length - 2 - prompt.Length);
        string shownInput = TrimLeft(input, inputBudget);
        string line = status + "  " + prompt + shownInput;
        if (line.Length <= width) return line;

        int statusBudget = Math.Max(0, width - 2 - prompt.Length - shownInput.Length);
        status = TrimRight(status, statusBudget);
        line = status.Length == 0
            ? prompt + shownInput
            : status + "  " + prompt + shownInput;
        if (line.Length <= width) return line;

        return TrimLeft(prompt + input, width);
    }

    private static string TrimRight(string value, int max)
    {
        if (max <= 0) return "";
        if (value.Length <= max) return value;
        if (max <= 2) return value.Substring(0, max);
        return value.Substring(0, max - 2) + "..";
    }

    private static string TrimLeft(string value, int max)
    {
        if (max <= 0) return "";
        if (value.Length <= max) return value;
        if (max <= 3) return value.Substring(value.Length - max, max);
        return "..." + value.Substring(value.Length - (max - 3), max - 3);
    }
}
