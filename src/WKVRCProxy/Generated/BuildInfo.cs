// Placeholder. build.ps1 overwrites this file with the real git
// short-SHA + build-time UTC timestamp before publish. This file IS
// tracked (NOT gitignored) so fresh-clone `dotnet build` and
// `dotnet test` (which bypass build.ps1) compile without errors.
//
// Running build.ps1 modifies this file in the working tree. `git
// checkout src/WKVRCProxy/Generated/BuildInfo.cs` reverts to the
// placeholder. CI builds via build.ps1 don't commit the modification.
namespace WKVRCProxy;
internal static class BuildInfo
{
    public const string GitSha = "<unknown>";
    public const string BuildTime = "<unknown>";
}
