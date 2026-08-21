using System.Collections.Generic;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using GdUnit4;
using Godot;
using Moq;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class EGlobalTests
{
    private readonly List<EditorPlugin> _createdPlugins = [];

    [AfterTest]
    public void Cleanup()
    {
        foreach (var plugin in _createdPlugins)
        {
            plugin.Free();
        }

        _createdPlugins.Clear();
    }

    private EditorPlugin CreatePluginBase()
    {
        var pluginBase = new EditorPlugin();
        _createdPlugins.Add(pluginBase);
        return pluginBase;
    }

    [TestCase]
    [RequireGodotRuntime]
    public void MatchesVersionExactMatchReturnsTrue()
    {
        Assertions.AssertBool(EGlobal.Instance.MatchesVersion("1.2.3", "1.2.3", new NullLogger())).IsTrue();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void MatchesVersionExactMismatchReturnsFalse()
    {
        Assertions.AssertBool(EGlobal.Instance.MatchesVersion("1.2.3", "1.2.4", new NullLogger())).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void MatchesVersionGreaterOrEqualSatisfiedReturnsTrue()
    {
        Assertions.AssertBool(EGlobal.Instance.MatchesVersion("2.0.0", ">1.0.0", new NullLogger())).IsTrue();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void MatchesVersionGreaterOrEqualBoundaryIsInclusive()
    {
        Assertions.AssertBool(EGlobal.Instance.MatchesVersion("1.0.0", ">1.0.0", new NullLogger())).IsTrue();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void MatchesVersionGreaterOrEqualNotSatisfiedReturnsFalse()
    {
        Assertions.AssertBool(EGlobal.Instance.MatchesVersion("0.9.0", ">1.0.0", new NullLogger())).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void MatchesVersionStripsSemverPrereleaseSuffix()
    {
        Assertions.AssertBool(EGlobal.Instance.MatchesVersion("1.2.3-beta.1", "1.2.3", new NullLogger())).IsTrue();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void MatchesVersionMalformedGivenVersionReturnsFalse()
    {
        Assertions.AssertBool(EGlobal.Instance.MatchesVersion("not-a-version", "1.0.0", new NullLogger())).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void MatchesVersionMalformedConditionReturnsFalse()
    {
        Assertions.AssertBool(EGlobal.Instance.MatchesVersion("1.0.0", "not-a-version", new NullLogger())).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void MatchesVersionMalformedGreaterThanConditionReturnsFalse()
    {
        Assertions.AssertBool(EGlobal.Instance.MatchesVersion("1.0.0", ">not-a-version", new NullLogger())).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void MatchesVersionEmptyGivenVersionReturnsFalse()
    {
        Assertions.AssertBool(EGlobal.Instance.MatchesVersion("", "1.0.0", new NullLogger())).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void MatchesVersionEmptyConditionReturnsFalse()
    {
        Assertions.AssertBool(EGlobal.Instance.MatchesVersion("1.0.0", "", new NullLogger())).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void DisableEPluginWithNullPluginIsNoOp()
    {
        var pluginBase = CreatePluginBase();
        var context = new PluginContext(null, pluginBase, new NullLogger());

        EGlobal.Instance.DisableEPlugin(context, false);

        Assertions.AssertObject(context.State).IsEqual(EEditorPluginState.Created);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void DisableEPluginAlreadyDeactivatedIsNoOp()
    {
        var pluginBase = CreatePluginBase();
        var mockPlugin = new Mock<IEEditorPlugin>();
        var context = new PluginContext(mockPlugin.Object, pluginBase, new NullLogger())
        {
            State = EEditorPluginState.Deactivated
        };

        EGlobal.Instance.DisableEPlugin(context, false);

        mockPlugin.Verify(p => p.CreateRecipe(It.IsAny<IEEditorPluginBuilder>()), Times.Never);
        Assertions.AssertObject(context.State).IsEqual(EEditorPluginState.Deactivated);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void DisableEPluginWithoutDependentsUninstallsAndDeactivates()
    {
        var pluginBase = CreatePluginBase();
        var mockPlugin = new Mock<IEEditorPlugin>();
        var context = new PluginContext(mockPlugin.Object, pluginBase, new NullLogger());

        EGlobal.Instance.DisableEPlugin(context, false);

        mockPlugin.Verify(p => p.CreateRecipe(It.IsAny<IEEditorPluginBuilder>()), Times.Once);
        Assertions.AssertObject(context.State).IsEqual(EEditorPluginState.Deactivated);
    }
}
