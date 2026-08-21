using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class IDotnetCliTests
{
    [TestCase]
    [RequireGodotRuntime]
    public void VersionTest()
    {
        var dotnetVersionManager = new DotnetVersionManager(new NullLogger(), false);
        var dotnetVersion = dotnetVersionManager.DotnetVersion;

        Assertions.AssertThat(dotnetVersion).IsNotEmpty();
    }
}
