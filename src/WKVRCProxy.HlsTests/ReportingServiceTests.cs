using System.Collections.Generic;
using WKVRCProxy.Core;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Services;
using Xunit;

namespace WKVRCProxy.HlsTests;

// Anonymous reporting is privacy-critical: a regression in the sanitizer leaks user data to a
// public Discord channel. Every shape of leak we know about gets a test that asserts it's gone.
// If you add a new sanitization rule, add a test here in the same pass.
public class ReportingServiceTests
{
    private static ReportingService NewService(string baseDir)
    {
        var sm = new SettingsManager(baseDir);
        var logger = new Logger(baseDir, "test", sm);
        return new ReportingService(logger, sm, "2026.4.27.0");
    }

    [Fact]
    public void Sanitize_StripsWindowsUsername()
    {
        using var dir = new TempDir();
        var rs = NewService(dir.Path);
        string input = @"Error in C:\Users\jdoe\AppData\Local\Temp\foo.log";
        string result = rs.Sanitize(input);
        Assert.DoesNotContain("jdoe", result);
        Assert.Contains("<USER>", result);
    }

    [Fact]
    public void Sanitize_StripsLinuxHomeDir()
    {
        using var dir = new TempDir();
        var rs = NewService(dir.Path);
        string result = rs.Sanitize("yt-dlp: error reading /home/alice/.config/yt-dlp/cookies.txt");
        Assert.DoesNotContain("alice", result);
        Assert.Contains("<USER>", result);
    }

    [Fact]
    public void Sanitize_StripsIPv4Literals()
    {
        using var dir = new TempDir();
        var rs = NewService(dir.Path);
        string result = rs.Sanitize("Connection to 192.168.1.42 refused; tried 8.8.8.8 too");
        Assert.DoesNotContain("192.168.1.42", result);
        Assert.DoesNotContain("8.8.8.8", result);
        Assert.Contains("<IP>", result);
    }

    [Fact]
    public void Sanitize_StripsLongTokenLikeStrings()
    {
        using var dir = new TempDir();
        var rs = NewService(dir.Path);
        // 64-char hex (looks like a session token)
        string token = new string('a', 64);
        string result = rs.Sanitize("Authorization: Bearer " + token);
        Assert.DoesNotContain(token, result);
        Assert.Contains("<TOKEN>", result);
    }

    [Fact]
    public void Sanitize_StripsDriveLetterPaths()
    {
        using var dir = new TempDir();
        var rs = NewService(dir.Path);
        string result = rs.Sanitize(@"Could not load D:\SecretProject\config.ini");
        Assert.DoesNotContain("SecretProject", result);
        Assert.Contains("<PATH>", result);
    }

    [Fact]
    public void Sanitize_StripsMachineHostname()
    {
        // Sanitize references Environment.MachineName at construction, so we can't really assert
        // a synthetic hostname unless we rely on the real one — the test machine's name won't
        // appear in our test input. So just ensure the regex itself works for the real hostname.
        using var dir = new TempDir();
        var rs = NewService(dir.Path);
        string mn = System.Environment.MachineName;
        if (mn.Length < 3) return; // Some CI hostnames are too short for the rule; skip.
        string result = rs.Sanitize("Failed on host " + mn + " in cluster.");
        Assert.DoesNotContain(mn, result);
    }

    [Fact]
    public void Sanitize_TruncatesLongInput()
    {
        using var dir = new TempDir();
        var rs = NewService(dir.Path);
        string input = new string('x', 1000);
        string result = rs.Sanitize(input);
        Assert.True(result.Length <= 500);
    }

    [Fact]
    public void ExtractDomain_DropsScheme_PathQuery_AndWww()
    {
        Assert.Equal("youtube.com",
            ReportingService.ExtractDomain("https://www.youtube.com/watch?v=abcdefg&list=xyz"));
        Assert.Equal("youtu.be",
            ReportingService.ExtractDomain("https://youtu.be/abcdefg?t=42"));
        Assert.Equal("vr-m.net",
            ReportingService.ExtractDomain("https://vr-m.net:8443/playlist.m3u8?token=secret"));
    }

    [Fact]
    public void ExtractDomain_HandlesGarbageGracefully()
    {
        Assert.Equal("unknown", ReportingService.ExtractDomain(null));
        Assert.Equal("unknown", ReportingService.ExtractDomain(""));
        Assert.Equal("unknown", ReportingService.ExtractDomain("not-a-url"));
    }

    [Fact]
    public void ExtractYouTubeVideoId_ParsesStandardWatchUrl()
    {
        Assert.Equal("dQw4w9WgXcQ",
            ReportingService.ExtractYouTubeVideoId("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=foo"));
    }

    [Fact]
    public void ExtractYouTubeVideoId_ParsesShortLink()
    {
        Assert.Equal("dQw4w9WgXcQ",
            ReportingService.ExtractYouTubeVideoId("https://youtu.be/dQw4w9WgXcQ?t=42"));
    }

    [Fact]
    public void ExtractYouTubeVideoId_ParsesShortsUrl()
    {
        Assert.Equal("dQw4w9WgXcQ",
            ReportingService.ExtractYouTubeVideoId("https://www.youtube.com/shorts/dQw4w9WgXcQ"));
    }

    [Fact]
    public void ExtractYouTubeVideoId_NullForNonYouTube()
    {
        Assert.Null(ReportingService.ExtractYouTubeVideoId("https://vrcdn.live/foo/bar.m3u8"));
    }

    [Fact]
    public void Sha256Short_IsDeterministicAnd12Chars()
    {
        string a = ReportingService.Sha256Short("hello");
        string b = ReportingService.Sha256Short("hello");
        string c = ReportingService.Sha256Short("world");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(12, a.Length);
        Assert.Matches("^[a-f0-9]{12}$", a);
    }

    [Fact]
    public void BuildPayload_HasRequiredFields_NoFullUrl()
    {
        using var dir = new TempDir();
        var rs = NewService(dir.Path);
        var ctx = new CascadeFailureContext
        {
            OriginalUrl = "https://www.youtube.com/watch?v=abcd1234efg&list=foo&t=42",
            Player = "avpro",
            ErrorSummary = "All resolution tiers failed.",
        };
        var p = rs.BuildPayload(ctx);

        Assert.Equal("youtube.com", p["urlDomain"]);
        Assert.Equal("avpro", p["player"]);
        Assert.Equal("AllStrategiesFailed", p["failureKind"]);
        Assert.NotNull(p["videoIdHashShort"]);
        Assert.NotNull(p["urlPathHashShort"]);
        // Critical privacy assertion: serialized payload must never contain the raw URL.
        string json = System.Text.Json.JsonSerializer.Serialize(p);
        Assert.DoesNotContain("watch?v=", json);
        Assert.DoesNotContain("abcd1234efg", json);
        Assert.DoesNotContain("list=foo", json);
    }

    [Fact]
    public void BuildPayload_LiveStreamDetected()
    {
        using var dir = new TempDir();
        var rs = NewService(dir.Path);
        var ctx = new CascadeFailureContext
        {
            OriginalUrl = "https://example.com/stream/playlist.m3u8",
            Player = "avpro",
        };
        var p = rs.BuildPayload(ctx);
        Assert.Equal("live", p["streamType"]);
    }

    [Fact]
    public void BuildPayload_ErrorSummaryIsSanitized()
    {
        using var dir = new TempDir();
        var rs = NewService(dir.Path);
        var ctx = new CascadeFailureContext
        {
            OriginalUrl = "https://youtu.be/abc",
            Player = "avpro",
            ErrorSummary = @"Error: cookies.txt at C:\Users\jdoe\.config not found",
        };
        var p = rs.BuildPayload(ctx);
        var summary = (string?)p["errorSummary"];
        Assert.NotNull(summary);
        Assert.DoesNotContain("jdoe", summary!);
    }
}
