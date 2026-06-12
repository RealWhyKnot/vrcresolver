using WKVRCProxy;
using Xunit;

namespace WKVRCProxy.Tests;

public class UpdateCommandTests
{
    [Fact]
    public void Default_command_registry_exposes_update_command()
    {
        TerminalCommandRegistry registry = TerminalCommandRegistry.CreateDefault();

        Assert.True(registry.TryGet("update", out TerminalCommand? command));
        Assert.NotNull(command);
        Assert.Equal("update", command!.Name);
    }

    [Fact]
    public void BuildStartInfo_launches_updater_with_internal_request_env()
    {
        string installDir = Path.Combine(Path.GetTempPath(), "WKVRCProxy");

        var startInfo = UpdateCommand.BuildStartInfo(installDir);

        Assert.Equal(Path.Combine(installDir, "WKVRCProxy.Updater.exe"), startInfo.FileName);
        Assert.Equal(installDir, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal("1", startInfo.Environment[UpdateCommand.UpdateRequestedEnvVar]);
    }
}
