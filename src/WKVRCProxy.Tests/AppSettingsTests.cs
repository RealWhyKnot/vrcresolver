using System.Runtime.Versioning;
using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

[SupportedOSPlatform("windows")]
public class AppSettingsTests
{
    [Fact]
    public void Defaults_EnableTerminalAndMaintenanceSettings()
    {
        var settings = new AppSettings().Normalize();

        Assert.True(settings.Terminal.StatusLine);
        Assert.True(settings.Terminal.Animations);
        Assert.True(settings.Maintenance.UpdateCheck);
        Assert.True(settings.Maintenance.CodecAutoInstall);
    }

    [Fact]
    public void IncludePrereleases_DefaultsOff()
    {
        var settings = new AppSettings().Normalize();
        Assert.False(settings.Maintenance.IncludePrereleases);
    }

    [Fact]
    public void IncludePrereleases_IsExposedAsToggle()
    {
        Assert.True(AppSettingsRegistry.TryFind("include-prereleases", out var setting));
        var settings = new AppSettings().Normalize();
        Assert.True(setting!.TrySet(settings, "on", out string err), err);
        Assert.True(settings.Maintenance.IncludePrereleases);
        Assert.True(setting.TrySet(settings, "off", out err), err);
        Assert.False(settings.Maintenance.IncludePrereleases);
    }

    [Fact]
    public void HelperSettings_AreNoLongerExposed()
    {
        Assert.False(AppSettingsRegistry.TryFind("sharing", out _));
        Assert.False(AppSettingsRegistry.TryFind("gpu-sharing", out _));
        Assert.False(AppSettingsRegistry.TryFind("gpu-limit", out _));
        Assert.False(AppSettingsRegistry.TryFind("gpu", out _));
        Assert.False(AppSettingsRegistry.TryFind("gpu-percent", out _));
        Assert.False(AppSettingsRegistry.TryFind("upload-limit", out _));
        Assert.False(AppSettingsRegistry.TryFind("allow-on-battery", out _));
        Assert.False(AppSettingsRegistry.TryFind("encoding-quality", out _));
    }

    [Fact]
    public void Clone_RepairsMissingSections()
    {
        var settings = new AppSettings
        {
            Terminal = null!,
            Relay = null!,
            Maintenance = null!,
        };

        AppSettings cloned = settings.Clone();

        Assert.NotNull(cloned.Terminal);
        Assert.NotNull(cloned.Relay);
        Assert.NotNull(cloned.Maintenance);
    }

    [Theory]
    [InlineData("secure-local-video")]
    [InlineData("status-line")]
    [InlineData("animations")]
    [InlineData("update-check")]
    [InlineData("include-prereleases")]
    [InlineData("video-support-updates")]
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
