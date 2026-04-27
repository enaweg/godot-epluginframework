using Enaweg.Plugin.Internal;
using GdUnit4;
using Godot;
using Moq;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class IDotnetCliTests
{
    [TestCase]
    [RequireGodotRuntime]
    public void VersionTest()
    {
        var mockPlugin = new Mock<IEEditorPlugin>();

        var cli = EGlobal.Instance.GetCli(mockPlugin.Object);

        var dotnetVersion = cli.DotnetVersion;

        Assertions.AssertThat(dotnetVersion).IsNotEmpty();
    }
}