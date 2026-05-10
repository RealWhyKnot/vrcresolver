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
        Assert.Equal(25, settings.Helper.GpuLimitPercent);
        Assert.Equal(0, settings.Helper.UploadLimitMbps);
        Assert.False(settings.Helper.AllowOnBattery);
        Assert.True(settings.Terminal.StatusLine);
        Assert.True(settings.Terminal.Animations);
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
    [InlineData("gpu-limit", "5", "5%")]
    [InlineData("gpu-limit", "37", "37%")]
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
    [InlineData("gpu-limit", "4")]
    [InlineData("gpu-limit", "76")]
    [InlineData("gpu-limit", "37%")]
    [InlineData("gpu-limit", "a lot")]
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
}
