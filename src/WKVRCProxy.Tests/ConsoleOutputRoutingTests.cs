using Xunit;

namespace WKVRCProxy.Tests;

public class ConsoleOutputRoutingTests
{
    [Fact]
    public void RuntimeConsoleWritesStayInsideRendererOrEarlyStartup()
    {
        string root = FindRepoRoot();
        string[] sourceRoots =
        {
            Path.Combine(root, "src", "WKVRCProxy"),
            Path.Combine(root, "src", "WKVRCProxy.Shared"),
        };

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Normalize(Path.Combine(root, "src", "WKVRCProxy", "Program.cs")),
            Normalize(Path.Combine(root, "src", "WKVRCProxy", "TerminalRenderer.cs")),
            Normalize(Path.Combine(root, "src", "WKVRCProxy.Shared", "ConsoleUx.cs")),
        };

        var offenders = new List<string>();
        foreach (string sourceRoot in sourceRoots)
        {
            foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = Normalize(file);
                if (allowed.Contains(normalized))
                    continue;

                int lineNumber = 0;
                foreach (string line in File.ReadLines(file))
                {
                    lineNumber++;
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal))
                        continue;

                    if (line.Contains("Console.WriteLine", StringComparison.Ordinal)
                        || line.Contains("Console.Write(", StringComparison.Ordinal)
                        || line.Contains("Console.Error.WriteLine", StringComparison.Ordinal))
                    {
                        offenders.Add(Path.GetRelativePath(root, file) + ":" + lineNumber + ": " + trimmed);
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Runtime console output must go through ConsoleUx or TerminalRenderer so the live prompt is cleared first."
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "WKVRCProxy.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate WKVRCProxy repo root.");
    }

    private static string Normalize(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
