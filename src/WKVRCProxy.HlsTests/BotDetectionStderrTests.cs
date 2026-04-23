using WKVRCProxy.Core.Services;
using Xunit;

namespace WKVRCProxy.HlsTests;

public class BotDetectionStderrTests
{
    [Fact]
    public void DetectsCanonicalYouTubeBotPhrase()
    {
        string stderr = "ERROR: [youtube] dQw4w9WgXcQ: Sign in to confirm you're not a bot. Use --cookies-from-browser or --cookies for the authentication.";
        Assert.True(ResolutionEngine.IsBotDetectionStderr(stderr));
    }

    [Fact]
    public void DetectsAlternatePhrasing()
    {
        string stderr = "Sign in to confirm you are not a bot";
        Assert.True(ResolutionEngine.IsBotDetectionStderr(stderr));
    }

    [Fact]
    public void DetectsCaseInsensitive()
    {
        string stderr = "SIGN IN TO CONFIRM YOU'RE NOT A BOT";
        Assert.True(ResolutionEngine.IsBotDetectionStderr(stderr));
    }

    [Fact]
    public void DetectsEmbeddedInMultiLineStderr()
    {
        string stderr = "WARNING: Unable to extract something\nERROR: Sign in to confirm you're not a bot\n[debug] extractor closed";
        Assert.True(ResolutionEngine.IsBotDetectionStderr(stderr));
    }

    [Fact]
    public void IgnoresUnrelatedError()
    {
        Assert.False(ResolutionEngine.IsBotDetectionStderr("HTTP Error 403: Forbidden"));
        Assert.False(ResolutionEngine.IsBotDetectionStderr("Video unavailable: This video is private."));
        Assert.False(ResolutionEngine.IsBotDetectionStderr("Connection timeout after 30s"));
    }

    [Fact]
    public void HandlesEmptyAndNullStderr()
    {
        Assert.False(ResolutionEngine.IsBotDetectionStderr(""));
        Assert.False(ResolutionEngine.IsBotDetectionStderr(null!));
    }

    [Fact]
    public void DetectsShortFragment()
    {
        // Some yt-dlp variants drop the leading "Sign in to" portion.
        string stderr = "confirm you're not a bot by visiting the link";
        Assert.True(ResolutionEngine.IsBotDetectionStderr(stderr));
    }

    [Fact]
    public void DetectsCurlyApostropheVariant()
    {
        // YouTube's real stderr uses U+2019 (right single quotation mark), not U+0027. Captured
        // verbatim from a live session log — if the detector regresses to strict ASCII matching,
        // MarkDomainRequiresPot stops firing and tier 1 keeps racing non-PO strategies forever.
        string stderr = "ERROR: [youtube] biU0f7DJmGU: Sign in to confirm you\u2019re not a bot. Use --cookies-from-browser or --cookies for the authentication.";
        Assert.True(ResolutionEngine.IsBotDetectionStderr(stderr));
    }
}
