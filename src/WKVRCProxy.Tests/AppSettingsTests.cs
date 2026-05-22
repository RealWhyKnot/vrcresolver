using System.Runtime.Versioning;
using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

[SupportedOSPlatform("windows")]
public class AppSettingsTests
{
    [Fact]
    public void Defaults_EnableSharingWithConservativeLimits()
    {
        var settings = new AppSettings().Normalize();

        Assert.True(settings.Helper.GpuSharing);
        Assert.Equal(0, settings.Helper.UploadLimitMbps);
        Assert.False(settings.Helper.AllowOnBattery);
        Assert.Equal("auto", settings.Helper.EncodingQuality);
        Assert.True(settings.Terminal.StatusLine);
        Assert.True(settings.Terminal.Animations);
    }

    [Fact]
    public void GpuLimitSetting_NoLongerExposed()
    {
        // Removed 2026-05-22: GpuLimitPercent was confusingly named (read as
        // "max % the helper uses" but actually a back-off sensitivity knob).
        // Helper now uses a hardcoded internal threshold; no user-facing knob.
        Assert.False(AppSettingsRegistry.TryFind("gpu-limit", out _));
        Assert.False(AppSettingsRegistry.TryFind("gpu", out _));
        Assert.False(AppSettingsRegistry.TryFind("gpu-percent", out _));
    }

    [Fact]
    public void Clone_RepairsMissingSections()
    {
        var settings = new AppSettings
        {
            Terminal = null!,
            Relay = null!,
            Maintenance = null!,
            Helper = null!,
        };

        AppSettings cloned = settings.Clone();

        Assert.NotNull(cloned.Terminal);
        Assert.NotNull(cloned.Relay);
        Assert.NotNull(cloned.Maintenance);
        Assert.NotNull(cloned.Helper);
        Assert.True(cloned.Helper.GpuSharing);
    }

    [Theory]
    [InlineData("upload-limit", "0", "automatic")]
    [InlineData("upload-limit", "5", "5 MB/s")]
    [InlineData("upload-limit", "12", "12 MB/s")]
    public void Settings_AcceptNumericInputsAndRenderUnits(string key, string input, string rendered)
    {
        Assert.True(AppSettingsRegistry.TryFind(key, out var setting));
        var settings = new AppSettings().Normalize();

        Assert.True(setting!.TrySet(settings, input, out string error), error);

        Assert.Equal(rendered, setting.Get(settings));
    }

    [Theory]
    [InlineData("upload-limit", "-1")]
    [InlineData("upload-limit", "501")]
    [InlineData("upload-limit", "12 MB/s")]
    [InlineData("upload-limit", "fast")]
    public void Settings_RejectOutOfRangeOrNonNumericValues(string key, string input)
    {
        Assert.True(AppSettingsRegistry.TryFind(key, out var setting));
        var settings = new AppSettings().Normalize();

        Assert.False(setting!.TrySet(settings, input, out string error));
        Assert.Contains("enter a number", error);
    }

    [Theory]
    [InlineData("sharing")]
    [InlineData("secure-local-video")]
    [InlineData("status-line")]
    [InlineData("animations")]
    [InlineData("update-check")]
    public void Settings_AcceptPlainToggles(string key)
    {
        Assert.True(AppSettingsRegistry.TryFind(key, out var setting));
        var settings = new AppSettings().Normalize();

        Assert.True(setting!.TrySet(settings, "off", out string offError), offError);
        Assert.Equal("off", setting.Get(settings));
        Assert.True(setting.TrySet(settings, "on", out string onError), onError);
        Assert.Equal("on", setting.Get(settings));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("fast")]
    [InlineData("balanced")]
    [InlineData("quality")]
    public void Settings_AcceptEncodingQuality(string value)
    {
        Assert.True(AppSettingsRegistry.TryFind("encoding-quality", out var setting));
        var settings = new AppSettings().Normalize();

        Assert.True(setting!.TrySet(settings, value, out string error), error);

        Assert.Equal(value, setting.Get(settings));
    }

    [Fact]
    public void Settings_RejectUnknownEncodingQuality()
    {
        Assert.True(AppSettingsRegistry.TryFind("encoding-quality", out var setting));
        var settings = new AppSettings().Normalize();

        Assert.False(setting!.TrySet(settings, "ultra", out string error));
        Assert.Contains("expected auto", error);
    }
}
