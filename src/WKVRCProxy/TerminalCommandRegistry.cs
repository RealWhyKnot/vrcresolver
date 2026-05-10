namespace WKVRCProxy;

internal sealed class TerminalCommandRegistry
{
    private readonly Dictionary<string, TerminalCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TerminalCommand> _primaryCommands = new();

    public IReadOnlyList<TerminalCommand> All => _primaryCommands;

    public static TerminalCommandRegistry CreateDefault()
    {
        var registry = new TerminalCommandRegistry();

        registry.Add(
            "help",
            "Show available terminal commands.",
            static (ctx, _, _) =>
            {
                ctx.Renderer.RenderHelp(ctx.Commands.All);
                return Task.CompletedTask;
            },
            "?");

        registry.Add(
            "settings",
            "List or change persisted watchdog settings.",
            TerminalSettingsCommand.ExecuteAsync,
            "config");

        registry.Add(
            "status",
            "Show what the proxy is doing right now.",
            static (ctx, _, _) =>
            {
                ctx.Renderer.RenderStatus(ctx.GetSnapshot(), ctx.MeshConnected());
                return Task.CompletedTask;
            },
            "stats",
            "activity",
            "dashboard");

        registry.Add(
            "history",
            "Show recent terminal commands.",
            static (ctx, _, _) =>
            {
                ctx.Renderer.RenderHistory(ctx.Session.RecentCommands);
                return Task.CompletedTask;
            });

        registry.Add(
            "permissions",
            "Show what this terminal is allowed to do.",
            static (ctx, _, _) =>
            {
                ctx.Renderer.Info("permissions: local watchdog commands only; shell execution is not exposed.");
                return Task.CompletedTask;
            });

        registry.Add(
            "tools",
            "Show managed watchdog subsystems.",
            static (ctx, _, _) =>
            {
                ctx.Renderer.RenderTools();
                return Task.CompletedTask;
            });

        registry.Add(
            "diagnostics",
            "Show useful support paths and session files.",
            static (ctx, _, _) =>
            {
                ctx.Renderer.RenderDiagnostics(ctx.Session);
                return Task.CompletedTask;
            },
            "diag",
            "paths");

        registry.Add(
            "clear",
            "Clear the terminal view.",
            static (ctx, _, _) =>
            {
                ctx.Renderer.ClearScreen();
                return Task.CompletedTask;
            },
            "cls");

        registry.Add(
            "quit",
            "Shut down WKVRCProxy cleanly.",
            static (ctx, _, _) =>
            {
                ctx.Renderer.Info("quit requested; shutting down.");
                ctx.RequestShutdown();
                return Task.CompletedTask;
            },
            "exit");

        return registry;
    }

    public void Add(
        string name,
        string description,
        Func<TerminalCommandContext, string, CancellationToken, Task> handler,
        params string[] aliases)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Command name is required.", nameof(name));
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        var command = new TerminalCommand(
            TerminalCommandLine.NormalizeVerb(name),
            description ?? "",
            handler,
            aliases.Select(TerminalCommandLine.NormalizeVerb).Where(a => a.Length > 0).ToArray());

        _primaryCommands.Add(command);
        _commands[command.Name] = command;
        foreach (string alias in command.Aliases)
            _commands[alias] = command;
    }

    public bool TryGet(string verb, out TerminalCommand? command)
    {
        return _commands.TryGetValue(TerminalCommandLine.NormalizeVerb(verb), out command);
    }

    public TerminalCompletion Complete(string input)
    {
        input ??= "";
        string trimmed = input.TrimStart();
        bool slash = trimmed.StartsWith("/", StringComparison.Ordinal);
        string token = slash ? trimmed.Substring(1) : trimmed;
        int tokenEnd = token.IndexOfAny(new[] { ' ', '\t' });
        if (tokenEnd >= 0)
            return TerminalCompletion.Empty;

        string prefix = TerminalCommandLine.NormalizeVerb(token);
        var matches = _primaryCommands
            .Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || c.Aliases.Any(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (prefix.Length == 0)
            return new TerminalCompletion("", matches);

        if (matches.Length == 1)
            return new TerminalCompletion((slash ? "/" : "") + matches[0].Name, Array.Empty<TerminalCommand>());

        return new TerminalCompletion("", matches);
    }
}

internal sealed class TerminalCommand
{
    private readonly Func<TerminalCommandContext, string, CancellationToken, Task> _handler;

    public TerminalCommand(
        string name,
        string description,
        Func<TerminalCommandContext, string, CancellationToken, Task> handler,
        IReadOnlyList<string> aliases)
    {
        Name = name;
        Description = description;
        _handler = handler;
        Aliases = aliases;
    }

    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<string> Aliases { get; }

    public Task ExecuteAsync(TerminalCommandContext context, string arguments, CancellationToken ct)
    {
        return _handler(context, arguments, ct);
    }
}

internal sealed class TerminalCommandContext
{
    public TerminalCommandContext(
        TerminalRenderer renderer,
        TerminalSessionStore session,
        TerminalCommandRegistry commands,
        Action requestShutdown,
        Func<WatchdogActivitySnapshot> getSnapshot,
        Func<bool> meshConnected)
    {
        Renderer = renderer;
        Session = session;
        Commands = commands;
        RequestShutdown = requestShutdown;
        GetSnapshot = getSnapshot;
        MeshConnected = meshConnected;
    }

    public TerminalRenderer Renderer { get; }
    public TerminalSessionStore Session { get; }
    public TerminalCommandRegistry Commands { get; }
    public Action RequestShutdown { get; }
    public Func<WatchdogActivitySnapshot> GetSnapshot { get; }
    public Func<bool> MeshConnected { get; }
}

internal readonly record struct TerminalCompletion(
    string Replacement,
    IReadOnlyList<TerminalCommand> Suggestions)
{
    public static TerminalCompletion Empty { get; } =
        new TerminalCompletion("", Array.Empty<TerminalCommand>());
}
