namespace WKVRCProxy;

internal static class WhyKnotEndpoints
{
    public const string ProxyHost = "proxy.whyknot.dev";
    public static readonly Uri LegacyApexDiscoveryUrl = new("https://whyknot.dev/");
    public static readonly Uri ReportUrl = new("https://proxy.whyknot.dev/api/report");

    public static Uri MeshWebSocketUrlForHost(string host)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0)
            throw new ArgumentException("mesh host is required", nameof(host));

        return new Uri("wss://" + host + "/mesh");
    }

    public static bool TryExtractDiscoveryRedirectHost(Uri location, out string host)
    {
        var baseUri = LegacyApexDiscoveryUrl;
        Uri absolute = location.IsAbsoluteUri ? location : new Uri(baseUri, location);
        host = absolute.Host;
        return host.Length > 0
            && !host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase);
    }
}
