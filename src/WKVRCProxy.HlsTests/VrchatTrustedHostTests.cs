using System.Runtime.Versioning;
using WKVRCProxy.Core.Services;
using Xunit;

namespace WKVRCProxy.HlsTests;

// The trusted-host table lives in ResolutionEngine and is the inverse of the relay-wrap gate:
// hosts ON the list are passed pristine to AVPro (already trusted), hosts OFF the list MUST be
// relay-wrapped or AVPro silently rejects them with "Loading failed". These tests lock the list
// against accidental edits — adding or removing a domain here is a VRChat-version-dependent change
// and should come with a log capture, not a drive-by refactor.
[SupportedOSPlatform("windows")]
public class VrchatTrustedHostTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc")]
    [InlineData("https://youtu.be/abc")]
    [InlineData("https://r5---sn-abc.googlevideo.com/videoplayback?x=1")]
    [InlineData("https://vod-progressive.akamaized.net/exp=1/file.mp4")]
    [InlineData("https://graph.facebook.com/v1/video")]
    [InlineData("https://video.xx.fbcdn.net/v/file.mp4")]
    [InlineData("https://api.hyperbeam.com/session")]
    [InlineData("https://app.hyperbeam.dev/session")]
    [InlineData("https://a.mixcloud.com/x.m4a")]
    [InlineData("https://dmc.nicovideo.jp/video/x.mp4")]
    [InlineData("https://soundcloud.com/x/y")]
    [InlineData("https://a1.sndcdn.com/stream.mp3")]
    [InlineData("https://ciel.topaz.chat/room")]
    [InlineData("https://www.twitch.tv/x/video")]
    [InlineData("https://video-edge.ttvnw.net/seg.ts")]
    [InlineData("https://usher.twitchcdn.net/playlist.m3u8")]
    [InlineData("https://stream.vrcdn.live/m3u8")]
    [InlineData("https://edge.vrcdn.video/m3u8")]
    [InlineData("https://cache.vrcdn.cloud/x.mp4")]
    [InlineData("https://player.vimeo.com/video/123")]
    [InlineData("https://v.youku.com/v_show/x")]
    public void Trusted_HostsArePristine(string url)
    {
        Assert.True(ResolutionEngine.IsVrchatTrustedHost(url), url + " should be on VRChat's trusted list");
    }

    [Theory]
    [InlineData("https://node1.whyknot.dev/api/proxy?q=abc")]    // the real-world failure that prompted the fix
    [InlineData("https://vr-m.net/p/9851.m3u8")]                // VRChat "movie world" host — NOT trusted (uses native-UA deny-list instead)
    [InlineData("https://cdn.example.com/stream.m3u8")]
    [InlineData("https://some-random-cdn.net/video.mp4")]
    public void Untrusted_HostsNeedWrap(string url)
    {
        Assert.False(ResolutionEngine.IsVrchatTrustedHost(url), url + " should require the relay wrap");
    }

    [Fact]
    public void InvalidUrl_NotTrusted()
    {
        Assert.False(ResolutionEngine.IsVrchatTrustedHost("not-a-url"));
        Assert.False(ResolutionEngine.IsVrchatTrustedHost(""));
    }
}
