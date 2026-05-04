using System.Runtime.Versioning;
using Xunit;

namespace WKVRCProxy.Tests;

// HostsManager.LineIsBypassEntry parses one hosts-file line at a time and
// returns true iff that line maps 127.0.0.1 to localhost.youtube.com. The
// matcher must be conservative: a comment line that mentions the marker
// host is not a bypass entry; nor is a wrong-IP line; nor is a line where
// the marker appears as a substring of a longer host. Earlier impls used a
// substring check on the whole line and false-positived on all of those.
[SupportedOSPlatform("windows")]
public class HostsManagerTests
{
    [Theory]
    [InlineData("127.0.0.1 localhost.youtube.com", true)]
    [InlineData("127.0.0.1\tlocalhost.youtube.com", true)]
    [InlineData("127.0.0.1   localhost.youtube.com   # WKVRCProxy", true)]
    [InlineData("127.0.0.1 localhost.youtube.com  # any other comment", true)]
    [InlineData("    127.0.0.1 localhost.youtube.com", true)]
    // Multiple hosts on one line — bypass marker present anywhere counts.
    [InlineData("127.0.0.1 some.other.host localhost.youtube.com extra.host", true)]
    // Case-insensitive on the host token (Windows DNS / hosts is
    // case-insensitive). Format-exact on the IP token.
    [InlineData("127.0.0.1 LocalHost.YouTube.com", true)]
    public void LineIsBypassEntry_RecognizesValidEntries(string line, bool expected)
    {
        Assert.Equal(expected, HostsManager.LineIsBypassEntry(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\t")]
    public void LineIsBypassEntry_RejectsBlankLines(string? line)
    {
        Assert.False(HostsManager.LineIsBypassEntry(line));
    }

    [Theory]
    // Comment line — even if it mentions our marker.
    [InlineData("# 127.0.0.1 localhost.youtube.com")]
    [InlineData("    # 127.0.0.1 localhost.youtube.com")]
    [InlineData("#127.0.0.1 localhost.youtube.com")]
    public void LineIsBypassEntry_IgnoresComments(string line)
    {
        Assert.False(HostsManager.LineIsBypassEntry(line));
    }

    [Theory]
    // Wrong IP — only 127.0.0.1 counts. A user pinning to 127.0.0.2 / a
    // different loopback IP / their LAN IP is not the bypass we'd manage.
    [InlineData("127.0.0.2 localhost.youtube.com")]
    [InlineData("0.0.0.0 localhost.youtube.com")]
    [InlineData("192.168.1.1 localhost.youtube.com")]
    [InlineData("::1 localhost.youtube.com")]
    public void LineIsBypassEntry_RejectsWrongIp(string line)
    {
        Assert.False(HostsManager.LineIsBypassEntry(line));
    }

    [Theory]
    // Marker is a SUBSTRING of a longer host — must NOT match.
    [InlineData("127.0.0.1 notlocalhost.youtube.com")]
    [InlineData("127.0.0.1 localhost.youtube.com.evil.com")]
    [InlineData("127.0.0.1 prefixlocalhost.youtube.com")]
    public void LineIsBypassEntry_RejectsSubstringMatches(string line)
    {
        Assert.False(HostsManager.LineIsBypassEntry(line));
    }

    [Theory]
    [InlineData("127.0.0.1")] // IP only, no host
    [InlineData("localhost.youtube.com")] // host only, no IP
    [InlineData("127.0.0.1 example.com")] // wrong host
    [InlineData("# WKVRCProxy hosts entry follows")] // narrative comment, no entry
    public void LineIsBypassEntry_RejectsMalformedOrUnrelated(string line)
    {
        Assert.False(HostsManager.LineIsBypassEntry(line));
    }

    [Fact]
    public void LineIsBypassEntry_StripsTrailingComment()
    {
        // The `#` introducer can come immediately after the host (no space).
        Assert.True(HostsManager.LineIsBypassEntry("127.0.0.1 localhost.youtube.com#WKVRCProxy"));
        // Comment in the middle short-circuits the rest of the line.
        Assert.False(HostsManager.LineIsBypassEntry("127.0.0.1 #localhost.youtube.com"));
    }
}
