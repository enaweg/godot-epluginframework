#if TOOLS
using System;
using System.IO;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>Which folders besides <c>addons/</c> hold plugin code when an update's build fails.</summary>
[TestSuite]
public class PluginCodeTests
{
    [TestCase]
    public static void ProjectsOfTheUpdatesRecipesArePluginCodeUnlessTheyHoldTheWholeProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "eplugin-code-" + Guid.NewGuid().ToString("N"), "game");
        Directory.CreateDirectory(Path.Combine(root, "addons", "plugin"));
        try
        {
            var journal = new UpdateJournal();
            journal.Recipes["plugin"] = new()
            {
                Old = new() { Projects = [new("res://libs/Old/Old.csproj", null, true)] },
                Applied = new() { Projects = [new("res://libs/Old/Old.csproj", null, true), new("../shared/Lib/Lib.csproj", null, true)] },
                Target = new() { Projects = [new("libs/New/New.csproj", null, true), new("res://Game.csproj", null, true), new("../Outer.csproj", null, true)] }
            };

            var roots = PluginCode.Roots(root, journal);

            var separator = Path.DirectorySeparatorChar;
            Assertions.AssertThat(roots).ContainsExactly(
                Path.Combine(root, "libs", "Old") + separator,
                Path.GetFullPath(Path.Combine(root, "..", "shared", "Lib")) + separator,
                Path.Combine(root, "libs", "New") + separator);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }
    }

    [TestCase]
    public static void AnUpdateWithoutRecipesHasNoPluginCodeOutsideTheAddons()
    {
        var root = Path.Combine(Path.GetTempPath(), "eplugin-code-" + Guid.NewGuid().ToString("N"));
        // no addons folder at all
        Assertions.AssertThat(PluginCode.Roots(root, new UpdateJournal())).IsEmpty();
    }
}
#endif
