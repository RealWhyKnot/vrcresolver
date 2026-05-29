using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

public class WhyKnotEndpointsTests
{
    [Fact]
    public void DefaultsUseProxyPublicHost()
    {
        Assert.Equal("proxy.whyknot.dev", WhyKnotEndpoints.ProxyHost);
        Assert.Equal(
            "wss://proxy.whyknot.dev/mesh",
            WhyKnotEndpoints.MeshWebSocketUrlForHost(WhyKnotEndpoints.ProxyHost).ToString());
        Assert.Equal("https://proxy.whyknot.dev/api/report", WhyKnotEndpoints.ReportUrl.ToString());
    }

    [Theory]
    [InlineData("https://proxy.whyknot.dev/", true, "proxy.whyknot.dev")]
    [InlineData("https://node1.whyknot.dev/", true, "node1.whyknot.dev")]
    [InlineData("https://node2.whyknot.dev/mesh", true, "node2.whyknot.dev")]
    [InlineData("https://whyknot.dev/", false, "whyknot.dev")]
    [InlineData("/mesh", false, "whyknot.dev")]
    public void DiscoveryRedirectHostAcceptsProxyAndLegacyAliases(
        string location,
        bool expected,
        string expectedHost)
    {
        bool ok = WhyKnotEndpoints.TryExtractDiscoveryRedirectHost(new Uri(location, UriKind.RelativeOrAbsolute), out string host);

        Assert.Equal(expected, ok);
        Assert.Equal(expectedHost, host);
    }

    [Fact]
    public void MeshWebSocketUrlStillSupportsLegacyNodeAliases()
    {
        Assert.Equal(
            "wss://node1.whyknot.dev/mesh",
            WhyKnotEndpoints.MeshWebSocketUrlForHost("node1.whyknot.dev").ToString());
    }
}
