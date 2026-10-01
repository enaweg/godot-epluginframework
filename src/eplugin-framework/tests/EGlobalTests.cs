using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
    public void PlainInvalidRetryRereadsVersionAndUnsupportedMarkerStaysBlocked()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eplugin-retry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PluginStateStore(Path.Combine(directory, "state.json"), new NullLogger());
            store.Load();
            store.TryCreateBaseline([]);
            store.TryRecordInvalid("plain-retry", null, "invalid_plugin_version");
            store.TryBeginAttempt("plain-blocked", "1.0", PersistedPluginState.Activated, out _);
            var global = (EGlobal)Activator.CreateInstance(typeof(EGlobal), nonPublic: true)!;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(EGlobal).GetField("_stateStore", flags)!.SetValue(global, store);
            string? installed = null;
            var observer = new PlainPluginObserver(store, () => new HashSet<string>(), _ => installed,
                _ => false, _ => false, new NullLogger());
            typeof(EGlobal).GetField("_plainPluginObserver", flags)!.SetValue(global, observer);
            var contexts = (List<PluginContext>)typeof(EGlobal).GetField("_contexts", flags)!.GetValue(global)!;
            foreach (var slug in new[] { "plain-retry", "plain-blocked" })
            {
                contexts.Add(new PluginContext(null, CreatePluginBase(), new NullLogger())
                {
                    Slug = slug,
                    State = EEditorPluginState.Error
                });
            }

            global.RetryFailedPlugins();
            Assertions.AssertBool(store.IsBlocked("plain-retry")).IsTrue();
            installed = "2.0.0";
            global.RetryFailedPlugins();
            Assertions.AssertBool(store.IsBlocked("plain-retry")).IsFalse();
            Assertions.AssertString(store.GetShared("plain-retry")!.Version).IsEqual("2.0.0");
            Assertions.AssertObject(store.GetShared("plain-retry")!.State).IsEqual(PersistedPluginState.Deactivated);
            Assertions.AssertBool(store.IsBlocked("plain-blocked")).IsTrue();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestCase]
    public void BaselineIncludesPlainContextsAndExcludesBlockedAndInvalidVersions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eplugin-baseline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PluginStateStore(Path.Combine(directory, "state.json"), new NullLogger());
            store.Load();
            store.TryRecordInvalid("blocked", null, "invalid_plugin_version");
            var global = (EGlobal)Activator.CreateInstance(typeof(EGlobal), nonPublic: true)!;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(EGlobal).GetField("_stateStore", flags)!.SetValue(global, store);
            var contexts = (List<PluginContext>)typeof(EGlobal).GetField("_contexts", flags)!.GetValue(global)!;
            foreach (var slug in new[] { "plain", "invalid", "blocked" })
            {
                contexts.Add(new PluginContext(null, CreatePluginBase(), new NullLogger())
                {
                    Slug = slug,
                    Metadata = new EEditorPluginMetadata { Version = slug == "invalid" ? " " : "1.0.0" },
                    State = EEditorPluginState.Activated
                });
            }

            typeof(EGlobal).GetMethod("CreateStateBaseline", flags)!.Invoke(global, null);
            Assertions.AssertString(store.GetShared("plain")!.Version).IsEqual("1.0.0");
            Assertions.AssertObject(store.GetShared("invalid")).IsNull();
            Assertions.AssertObject(store.GetShared("blocked")).IsNull();
            Assertions.AssertString(store.GetLocal("invalid")!.Reason).IsEqual("invalid_plugin_version");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
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

    [TestCase]
    [RequireGodotRuntime]
    // NOTE: optional dependencies that ARE enabled cannot be covered here — resolution calls
    // EditorInterface.Singleton.IsPluginEnabled, which is unavailable in the headless test runtime.
    public void ResolveOptionalRecipesWithoutOptionalDependenciesReturnsEmpty()
    {
        var pluginBase = CreatePluginBase();
        var context = new PluginContext(null, pluginBase, new NullLogger());
        var recipe = new EEditorPluginRecipe();

        var resolved = EGlobal.Instance.ResolveOptionalRecipes(context, recipe);

        Assertions.AssertInt(resolved.Count).IsEqual(0);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ResolveOptionalRecipesSkipsIgnoredSlugWithoutQueryingTheEditor()
    {
        var pluginBase = CreatePluginBase();
        var context = new PluginContext(null, pluginBase, new NullLogger());
        var builder = EEditorPluginBuilder.Create();
        builder.AddOptionalPluginDependency("some-plugin", null, optional => optional.AddNuget("ZLogger"));

        // the ignored slug is treated as not enabled, short-circuiting before EditorInterface is asked --
        // this is how the pre-enable baseline is reconstructed when no snapshot is available.
        var resolved = EGlobal.Instance.ResolveOptionalRecipes(context, builder.PluginRecipe, "some-plugin");

        Assertions.AssertInt(resolved.Count).IsEqual(0);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ResolveOptionalRecipesTreatsAssumedEnabledSlugAsSatisfied()
    {
        var pluginBase = CreatePluginBase();
        var context = new PluginContext(null, pluginBase, new NullLogger());
        var builder = EEditorPluginBuilder.Create();
        builder.AddOptionalPluginDependency("some-plugin", null, optional => optional.AddNuget("ZLogger"));

        // the assumed slug counts as enabled without asking EditorInterface -- this is how the recipes
        // installed for a plugin that is being disabled right now are reconstructed.
        var resolved = EGlobal.Instance.ResolveOptionalRecipes(context, builder.PluginRecipe,
            assumeEnabledSlug: "some-plugin");

        Assertions.AssertInt(resolved.Count).IsEqual(1);
        Assertions.AssertString(resolved[0].Slug).IsEqual("some-plugin");
    }

    [TestCase]
    [RequireGodotRuntime]
    public void DisableEPluginClearsAppliedOptionalDependencySnapshot()
    {
        var pluginBase = CreatePluginBase();
        var mockPlugin = new Mock<IEEditorPlugin>();
        var context = new PluginContext(mockPlugin.Object, pluginBase, new NullLogger())
        {
            AppliedOptionalDependencies =
                [new EEditorPluginRecipe.OptionalPlugin("some-plugin", null, new EEditorPluginRecipe())]
        };

        EGlobal.Instance.DisableEPlugin(context, false);

        Assertions.AssertObject(context.AppliedOptionalDependencies).IsNull();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ResolveOptionalRecipesSkipsDependencyInErrorWhenReadinessIsRequired()
    {
        var global = (EGlobal)Activator.CreateInstance(typeof(EGlobal), nonPublic: true)!;
        var contexts = (List<PluginContext>)typeof(EGlobal)
            .GetField("_contexts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(global)!;
        contexts.Add(new PluginContext(new Mock<IEEditorPlugin>().Object, CreatePluginBase(), new NullLogger())
        {
            Slug = "some-plugin",
            State = EEditorPluginState.Error
        });
        var context = new PluginContext(null, CreatePluginBase(), new NullLogger());
        var builder = EEditorPluginBuilder.Create();
        builder.AddOptionalPluginDependency("some-plugin", null, optional => optional.AddNuget("ZLogger"));

        // the failed plugin is rejected before EditorInterface is asked whether it is enabled
        var resolved = global.ResolveOptionalRecipes(context, builder.PluginRecipe, requireReady: true);

        Assertions.AssertInt(resolved.Count).IsEqual(0);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void OrderRetriesPutsFailedDependencyBeforeItsDependant()
    {
        var global = (EGlobal)Activator.CreateInstance(typeof(EGlobal), nonPublic: true)!;
        var contexts = (List<PluginContext>)typeof(EGlobal)
            .GetField("_contexts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(global)!;
        var dependant = new Mock<IEEditorPlugin>();
        dependant.Setup(p => p.CreateRecipe(It.IsAny<IEEditorPluginBuilder>()))
            .Callback<IEEditorPluginBuilder>(b => b.AddPluginDependency("b_dependency"));
        contexts.Add(new PluginContext(dependant.Object, CreatePluginBase(), new NullLogger()) { Slug = "a_dependant" });
        contexts.Add(new PluginContext(new Mock<IEEditorPlugin>().Object, CreatePluginBase(), new NullLogger())
            { Slug = "b_dependency" });
        LocalPluginAttempt Attempt(string slug) => new(Guid.NewGuid(), slug, "1.0", PersistedPluginState.Activated,
            PersistedPluginState.Failed, "recipe_operation_failed");

        var ordered = global.OrderRetries([Attempt("a_dependant"), Attempt("b_dependency"), Attempt("c_other")]);

        Assertions.AssertString(string.Join(",", ordered.Select(a => a.Slug)))
            .IsEqual("b_dependency,a_dependant,c_other");
        // ordering must not leave a recipe behind; the retry itself creates it
        Assertions.AssertBool(contexts[0].IsRecipeCreated).IsFalse();
    }
}
