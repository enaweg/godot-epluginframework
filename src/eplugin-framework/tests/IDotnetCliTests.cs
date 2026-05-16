using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using GdUnit4;
using Moq;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class IDotnetCliTests
{
    [TestCase]
    [RequireGodotRuntime]
    public void VersionTest()
    {
        var mockPlugin = new Mock<EPluginPlugin>();

        EGlobal.Instance.Initialize(mockPlugin.Object,
            new GenericLoggerFactory(category => new GodotConsoleLogger(category)));

        var dotnetVersion = EGlobal.Instance.CliService.DotnetVersion;

        Assertions.AssertThat(dotnetVersion).IsNotEmpty();
    }
}