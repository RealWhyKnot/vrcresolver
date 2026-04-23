using WKVRCProxy.Core.Services;
using Xunit;

namespace WKVRCProxy.HlsTests;

// Regression guard for the PO token pipeline. The client previously minted PO tokens with a
// fabricated visitor_data string ("wkvrcproxy") and spliced them into yt-dlp via
// --extractor-args youtube:po_token=web.gvs+TOKEN — which YouTube rejected, because the token
// binding didn't match yt-dlp's real visitor_data. The fix is to hand PO resolution off to the
// bgutil yt-dlp plugin so yt-dlp fetches the token itself with the correct binding.
//
// These tests cover the pure arg-building shape; the filesystem/port readiness gates live in
// the instance method and are exercised by integration runs against a real sidecar.
public class BgutilPluginArgsTests
{
    [Fact]
    public void ReturnsEmpty_WhenPortIsZero()
    {
        var args = ResolutionEngine.BuildBgutilPluginArgs(@"C:\tools\yt-dlp-plugins", 0);
        Assert.Empty(args);
    }

    [Fact]
    public void ReturnsEmpty_WhenPortIsNegative()
    {
        var args = ResolutionEngine.BuildBgutilPluginArgs(@"C:\tools\yt-dlp-plugins", -1);
        Assert.Empty(args);
    }

    [Fact]
    public void ReturnsEmpty_WhenPluginDirIsNull()
    {
        var args = ResolutionEngine.BuildBgutilPluginArgs(null!, 4416);
        Assert.Empty(args);
    }

    [Fact]
    public void ReturnsEmpty_WhenPluginDirIsEmpty()
    {
        var args = ResolutionEngine.BuildBgutilPluginArgs("", 4416);
        Assert.Empty(args);
    }

    [Fact]
    public void ReturnsEmpty_WhenPluginDirIsWhitespace()
    {
        var args = ResolutionEngine.BuildBgutilPluginArgs("   ", 4416);
        Assert.Empty(args);
    }

    [Fact]
    public void IncludesPluginDirsFlag_WhenReady()
    {
        var args = ResolutionEngine.BuildBgutilPluginArgs(@"C:\tools\yt-dlp-plugins", 4416);
        int idx = args.IndexOf("--plugin-dirs");
        Assert.True(idx >= 0, "--plugin-dirs flag must be present");
        Assert.Equal(@"C:\tools\yt-dlp-plugins", args[idx + 1]);
    }

    [Fact]
    public void IncludesBgutilExtractorArgsOnItsOwnScope()
    {
        // bgutil's scope (youtubepot-bgutilhttp) MUST be emitted as its own --extractor-args flag.
        // If it gets packed after a semicolon into the youtube: scope, yt-dlp treats the whole
        // "youtubepot-bgutilhttp:base_url=..." substring as a youtube extractor key, so the plugin
        // silently falls back to the hardcoded 127.0.0.1:4416 default.
        var args = ResolutionEngine.BuildBgutilPluginArgs(@"C:\tools\yt-dlp-plugins", 22361);
        int bgutilIdx = args.FindIndex(a => a != null && a.StartsWith("youtubepot-bgutilhttp:", System.StringComparison.Ordinal));
        Assert.True(bgutilIdx > 0, "bgutil extractor-args value must be present");
        Assert.Equal("--extractor-args", args[bgutilIdx - 1]);
        Assert.Equal("youtubepot-bgutilhttp:base_url=http://localhost:22361", args[bgutilIdx]);
    }

    [Fact]
    public void IncludesYoutubeScopeWithPlayerJsVariantMain()
    {
        // player_js_variant=main is a youtube-scope key and must stay on the youtube: scope.
        var args = ResolutionEngine.BuildBgutilPluginArgs(@"C:\tools\yt-dlp-plugins", 4416);
        int youtubeIdx = args.FindIndex(a => a != null && a.StartsWith("youtube:", System.StringComparison.Ordinal));
        Assert.True(youtubeIdx > 0, "youtube-scope extractor-args value must be present");
        Assert.Equal("--extractor-args", args[youtubeIdx - 1]);
        Assert.Contains("player_js_variant=main", args[youtubeIdx]);
    }

    [Fact]
    public void BgutilAndYoutubeScopesNeverShareOneString()
    {
        // Regression guard: the previous implementation packed both scopes into a single string
        // with a semicolon separator, which made the plugin ignore our custom base_url. If any
        // single arg contains both prefixes, we've regressed.
        var args = ResolutionEngine.BuildBgutilPluginArgs(@"C:\tools\yt-dlp-plugins", 4416);
        foreach (var a in args)
        {
            if (a == null) continue;
            bool hasYoutube = a.StartsWith("youtube:", System.StringComparison.Ordinal);
            bool hasBgutil = a.Contains("youtubepot-bgutilhttp:");
            Assert.False(hasYoutube && hasBgutil,
                "youtube and youtubepot-bgutilhttp scopes must be on separate --extractor-args flags, got: " + a);
        }
    }

    [Fact]
    public void NeverEmitsManualPoTokenString()
    {
        // Regression guard: the old path built "youtube:po_token=web.gvs+TOKEN" from a manually
        // fetched token. That binding was wrong. Make sure the new path never regresses to it.
        var args = ResolutionEngine.BuildBgutilPluginArgs(@"C:\tools\yt-dlp-plugins", 4416);
        foreach (var a in args)
        {
            Assert.DoesNotContain("po_token=", a);
            Assert.DoesNotContain("web.gvs+", a);
        }
    }

    [Fact]
    public void EmitsExactlySixArgsWhenReady()
    {
        // Current shape: [--plugin-dirs, <path>, --extractor-args, <youtube-scope-kv>,
        //                 --extractor-args, <bgutil-scope-kv>]. If this grows or shrinks, callers
        // that AddRange the result onto a larger args list may shift later positional arguments —
        // make the count change a deliberate review moment.
        var args = ResolutionEngine.BuildBgutilPluginArgs(@"C:\tools\yt-dlp-plugins", 4416);
        Assert.Equal(6, args.Count);
    }

    [Fact]
    public void FlagsAndValuesAreInterleavedCorrectly()
    {
        // yt-dlp expects each flag immediately followed by its value. If we ever reorder the
        // emitted list (e.g. grouping all flags before all values) it parses as garbage.
        var args = ResolutionEngine.BuildBgutilPluginArgs(@"C:\tools\yt-dlp-plugins", 4416);
        for (int i = 0; i < args.Count; i += 2)
        {
            Assert.StartsWith("--", args[i]);
            Assert.DoesNotMatch("^--", args[i + 1]);
        }
    }
}
