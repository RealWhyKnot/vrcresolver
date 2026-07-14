using System.Text.Json;
using VrcResolver;
using Xunit;

namespace VrcResolver.Tests;

// Pins the prerelease-aware release picker. The startup nudge
// (UpdateCheck.cs) and the standalone Updater binary use parallel
// implementations; both must agree on the same "highest version, regardless
// of publish order" rule so the user never sees one channel tell them about
// an update the other refuses to install.
public class UpdateCheckPrereleaseTests
{
    [Fact]
    public void PickHighestFromList_PicksHighestVersion_NotMostRecentlyPublished()
    {
        // GitHub returns most-recently-published first. A stable patch
        // (2026.5.10.5) published yesterday must still beat a prerelease
        // (2026.6.0.0-beta) published today when the user has opted in --
        // we want them on the newest *version*, not the newest tag.
        string json = """
        [
          { "tag_name": "v2026.6.0.0-beta", "html_url": "https://example.test/beta", "prerelease": true },
          { "tag_name": "v2026.5.10.5",      "html_url": "https://example.test/p5",   "prerelease": false }
        ]
        """;
        using var doc = JsonDocument.Parse(json);

        var pick = UpdateCheck.PickHighestFromList(doc.RootElement);

        Assert.NotNull(pick);
        Assert.Equal("v2026.6.0.0-beta", pick!.Value.tag);
        Assert.True(pick.Value.isPrerelease);
    }

    [Fact]
    public void PickHighestFromList_PrefersStableOverPrereleaseOnVersionTie()
    {
        // 2026.5.10.0 and 2026.5.10.0-pre1 parse to the same numeric
        // version (the -pre1 suffix is stripped before Version.TryParse).
        // The tie-breaker prefers the non-prerelease so an opted-in user
        // never gets prompted to install a prerelease when an equivalent
        // stable also exists.
        string json = """
        [
          { "tag_name": "v2026.5.10.0-pre1", "html_url": "https://example.test/pre1", "prerelease": true },
          { "tag_name": "v2026.5.10.0",      "html_url": "https://example.test/p0",   "prerelease": false }
        ]
        """;
        using var doc = JsonDocument.Parse(json);

        var pick = UpdateCheck.PickHighestFromList(doc.RootElement);

        Assert.NotNull(pick);
        Assert.Equal("v2026.5.10.0", pick!.Value.tag);
        Assert.False(pick.Value.isPrerelease);
    }

    [Fact]
    public void PickHighestFromList_StableEntryStaysWinnerEvenWhenPrereleaseAppearsLast()
    {
        string json = """
        [
          { "tag_name": "v2026.5.10.0",      "html_url": "https://example.test/p0",   "prerelease": false },
          { "tag_name": "v2026.5.10.0-pre1", "html_url": "https://example.test/pre1", "prerelease": true }
        ]
        """;
        using var doc = JsonDocument.Parse(json);

        var pick = UpdateCheck.PickHighestFromList(doc.RootElement);

        Assert.NotNull(pick);
        Assert.Equal("v2026.5.10.0", pick!.Value.tag);
        Assert.False(pick.Value.isPrerelease);
    }

    [Fact]
    public void PickHighestFromList_ReturnsNullForEmptyArray()
    {
        string json = "[]";
        using var doc = JsonDocument.Parse(json);

        Assert.Null(UpdateCheck.PickHighestFromList(doc.RootElement));
    }

    [Fact]
    public void PickHighestFromList_SkipsEntriesWithUnparsableTags()
    {
        string json = """
        [
          { "tag_name": "garbage-tag", "html_url": "x", "prerelease": false },
          { "tag_name": "v2026.5.10.0", "html_url": "y", "prerelease": false }
        ]
        """;
        using var doc = JsonDocument.Parse(json);

        var pick = UpdateCheck.PickHighestFromList(doc.RootElement);

        Assert.NotNull(pick);
        Assert.Equal("v2026.5.10.0", pick!.Value.tag);
    }

    [Fact]
    public void PickHighestFromList_StripsTrailingDevSuffixBeforeComparing()
    {
        // Build script stamps -XXXX dev suffixes; the prerelease channel
        // may emit these. Comparison happens on the numeric part only.
        string json = """
        [
          { "tag_name": "v2026.5.10.0-AAAA", "html_url": "x", "prerelease": true },
          { "tag_name": "v2026.5.9.5",        "html_url": "y", "prerelease": false }
        ]
        """;
        using var doc = JsonDocument.Parse(json);

        var pick = UpdateCheck.PickHighestFromList(doc.RootElement);

        Assert.NotNull(pick);
        Assert.Equal("v2026.5.10.0-AAAA", pick!.Value.tag);
    }
}
