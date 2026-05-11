namespace WKVRCProxy;

internal enum HelperEncodingQuality
{
    Auto,
    Fast,
    Balanced,
    Quality,
}

internal static class HelperEncodingQualityNames
{
    public const string Auto = "auto";
    public const string Fast = "fast";
    public const string Balanced = "balanced";
    public const string Quality = "quality";

    public static string Format(HelperEncodingQuality quality)
    {
        return quality switch
        {
            HelperEncodingQuality.Fast => Fast,
            HelperEncodingQuality.Balanced => Balanced,
            HelperEncodingQuality.Quality => Quality,
            _ => Auto,
        };
    }

    public static HelperEncodingQuality ParseOrAuto(string? value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            Fast => HelperEncodingQuality.Fast,
            Balanced => HelperEncodingQuality.Balanced,
            "balance" => HelperEncodingQuality.Balanced,
            Quality => HelperEncodingQuality.Quality,
            Auto => HelperEncodingQuality.Auto,
            "" => HelperEncodingQuality.Auto,
            _ => HelperEncodingQuality.Auto,
        };
    }

    public static bool TryParseUserValue(string? value, out HelperEncodingQuality quality, out string error)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        quality = ParseOrAuto(value);
        if (value is Auto or Fast or Balanced or "balance" or Quality)
        {
            error = "";
            return true;
        }

        error = "expected auto, fast, balanced, or quality";
        return false;
    }
}
