using System.IO;
using System.Reflection;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Logging;
using GdUnit4;
using Godot;

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

    [TestCase]
    [RequireGodotRuntime]
    public void ProjectPathUsesResolvedSolutionName()
    {
        var projectRoot = ProjectSettings.GlobalizePath("res://");
        var expectedPath = Path.Combine(projectRoot, "EPlugin Framework.csproj");
        var projectPathField = typeof(DotnetCliBase).GetField("GodotProjectPath",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (DotnetCliBase cli in new DotnetCliBase[]
                 {
                     new DotnetCli9(new NullLogger(), false),
                     new DotnetCli10(new NullLogger(), false)
                 })
        {
            Assertions.AssertThat((string)projectPathField.GetValue(cli)!).IsEqual(expectedPath);
        }
    }
}
