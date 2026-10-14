using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using GdUnit4;
using Godot;
using Moq;

namespace Enaweg.Plugin.Tests;

/// <summary>Transitions, baselines, retries and optional recipe resolution of <see cref="EGlobal"/>.</summary>
[TestSuite]
[RequireGodotRuntime]
public class EGlobalTests
{
    private LifecycleFixture _fixture = null!;
    private readonly List<EditorPlugin> _createdPlugins = [];

    [BeforeTest]
    public void Setup() => _fixture = new LifecycleFixture();

    [AfterTest]
    public void Cleanup()
    {
        foreach (var plugin in _createdPlugins) plugin.Free();
        _createdPlugins.Clear();
        _fixture.Dispose();
    }

    private PluginContext Context(string slug, IEEditorPlugin? plugin = null, string? version = "1.0.0",
        EEditorPluginState state = EEditorPluginState.Activated)
    {
        var pluginBase = new EditorPlugin();
        _createdPlugins.Add(pluginBase);
        return new PluginContext(plugin, pluginBase, new NullLogger())
        {
            Slug = slug, State = state, Metadata = version is null ? null : new EEditorPluginMetadata { Version = version }
        };
    }

    [TestCase]
    public void PlainInvalidRetryRereadsVersionAndUnsupportedMarkerStaysBlocked()
    {
        var store = _fixture.Store;
        store.TryRecordInvalid("plain-retry", null, "invalid_plugin_version");
        store.TryBeginAttempt("plain-blocked", "1.0", PersistedPluginState.Activated, out _);
        string? installed = null;
        _fixture.Global.PlainPluginObserver = new PlainPluginObserver(store, () => new HashSet<string>(), _ => installed,
            _ => false, _ => false, new NullLogger());
        foreach (var slug in new[] { "plain-retry", "plain-blocked" })
        {
            _fixture.Global.Contexts.Add(Context(slug, version: null, state: EEditorPluginState.Error));
        }

        _fixture.Global.RetryFailedPlugins();
        Assertions.AssertBool(store.IsBlocked("plain-retry")).IsTrue();
        installed = "2.0.0";
        _fixture.Global.RetryFailedPlugins();
        Assertions.AssertBool(store.IsBlocked("plain-retry")).IsFalse();
        Assertions.AssertString(store.GetShared("plain-retry")!.Version).IsEqual("2.0.0");
        Assertions.AssertObject(store.GetShared("plain-retry")!.State).IsEqual(PersistedPluginState.Deactivated);
        Assertions.AssertBool(store.IsBlocked("plain-blocked")).IsTrue();
    }

    [TestCase]
    public void PluginRejoiningItsOwnTransitionIsNotBlockedByItsMarker()
    {
        var store = _fixture.Store;
        store.TryBeginAttempt("other-blocked", "1.0", PersistedPluginState.Activated, out _);
        bool Join(PluginContext context) => _fixture.Global.JoinTransition(context, PersistedPluginState.Deactivated);
        var disabled = Context("disabled");
        var dependant = Context("dependant");

        // Disabling a plugin with a dependant: the plugin starts the transition, its dependant joins, then the
        // plugin resumes once the dependant is done.
        Assertions.AssertBool(Join(disabled)).IsTrue();
        Assertions.AssertBool(Join(dependant)).IsTrue();
        Assertions.AssertBool(Join(disabled)).IsTrue();
        Assertions.AssertString(store.GetLocal("disabled")!.Reason).IsEqual("attempt_in_progress");
        Assertions.AssertObject(disabled.State).IsEqual(EEditorPluginState.Activated);
        // A marker of another attempt still blocks.
        var blocked = Context("other-blocked");
        Assertions.AssertBool(Join(blocked)).IsFalse();
        Assertions.AssertObject(blocked.State).IsEqual(EEditorPluginState.Error);
    }

    [TestCase]
    public void HeldTransitionStaysOpenUntilReleased()
    {
        var global = _fixture.Global;
        var dependant = Context("dependant");

        // A plain plugin was disabled: its dependant is disabled while further plugins may still join.
        global.TransitionHold = 1;
        global.JoinTransition(dependant, PersistedPluginState.Deactivated);
        dependant.State = EEditorPluginState.Deactivated;
        Assertions.AssertBool(global.FinishTransition()).IsFalse();
        Assertions.AssertString(_fixture.Store.GetLocal("dependant")!.Reason).IsEqual("attempt_in_progress");

        global.TransitionHold = 0;
        Assertions.AssertBool(global.FinishTransition()).IsTrue();
        Assertions.AssertObject(_fixture.Store.GetLocal("dependant")).IsNull();
        Assertions.AssertObject(_fixture.Store.GetShared("dependant")!.State).IsEqual(PersistedPluginState.Deactivated);
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("build");
    }

    [TestCase]
    public void BaselineIncludesPlainContextsAndExcludesBlockedAndInvalidVersions()
    {
        // a project without a state file yet
        var store = new PluginStateStore(System.IO.Path.Combine(_fixture.Root, "fresh", "state.json"), new NullLogger());
        store.Load();
        store.TryRecordInvalid("blocked", null, "invalid_plugin_version");
        var global = new EGlobal(_fixture.Host) { StateStore = store };
        foreach (var slug in new[] { "plain", "invalid", "blocked" })
        {
            global.Contexts.Add(Context(slug, version: slug == "invalid" ? " " : "1.0.0"));
        }

        global.CreateStateBaseline();
        Assertions.AssertString(store.GetShared("plain")!.Version).IsEqual("1.0.0");
        Assertions.AssertObject(store.GetShared("invalid")).IsNull();
        Assertions.AssertObject(store.GetShared("blocked")).IsNull();
        Assertions.AssertString(store.GetLocal("invalid")!.Reason).IsEqual("invalid_plugin_version");
    }

    [TestCase]
    public void DisableEPluginWithNullPluginIsNoOp()
    {
        var context = Context("plain", state: EEditorPluginState.Created);

        _fixture.Global.DisableEPlugin(context, false);

        Assertions.AssertObject(context.State).IsEqual(EEditorPluginState.Created);
    }

    [TestCase]
    public void DisableEPluginAlreadyDeactivatedIsNoOp()
    {
        var mockPlugin = new Mock<IEEditorPlugin>();
        var context = Context("plugin", mockPlugin.Object, state: EEditorPluginState.Deactivated);

        _fixture.Global.DisableEPlugin(context, false);

        mockPlugin.Verify(p => p.CreateRecipe(It.IsAny<IEEditorPluginBuilder>()), Times.Never);
        Assertions.AssertObject(context.State).IsEqual(EEditorPluginState.Deactivated);
    }

    [TestCase]
    public void DisableEPluginClearsAppliedOptionalDependencySnapshot()
    {
        var context = Context("plugin", new Mock<IEEditorPlugin>().Object);
        context.AppliedOptionalDependencies = [new EEditorPluginRecipe.OptionalPlugin("some-plugin", null, new EEditorPluginRecipe())];
        _fixture.Global.Contexts.Add(context);

        _fixture.Global.DisableEPlugin(context);

        Assertions.AssertObject(context.State).IsEqual(EEditorPluginState.Deactivated);
        Assertions.AssertObject(context.AppliedOptionalDependencies).IsNull();
    }

    [TestCase]
    public void ResolveOptionalRecipesWithoutOptionalDependenciesReturnsEmpty()
    {
        var resolved = _fixture.Global.ResolveOptionalRecipes(Context("plugin"), new EEditorPluginRecipe());

        Assertions.AssertInt(resolved.Count).IsEqual(0);
    }

    [TestCase]
    public void ResolveOptionalRecipesFollowsTheEditorUnlessASlugIsIgnoredOrAssumed()
    {
        _fixture.AddPlainPlugin("enabled");
        _fixture.AddPlainPlugin("disabled");
        _fixture.Enable("enabled");
        var builder = EEditorPluginBuilder.Create();
        builder.AddOptionalPluginDependency("enabled", null, optional => optional.AddNuget("A"));
        builder.AddOptionalPluginDependency("disabled", null, optional => optional.AddNuget("B"));
        var context = Context("plugin");
        string Resolve(string? ignore = null, string? assume = null) => string.Join(",", _fixture.Global
            .ResolveOptionalRecipes(context, builder.PluginRecipe, ignore, assume).Select(o => o.Slug));

        Assertions.AssertString(Resolve()).IsEqual("enabled");
        // the baseline before "enabled" was enabled, and what was installed while "disabled" was still enabled
        Assertions.AssertString(Resolve(ignore: "enabled")).IsEqual("");
        Assertions.AssertString(Resolve(assume: "disabled")).IsEqual("enabled,disabled");
    }

    [TestCase]
    public void ResolveOptionalRecipesSkipsDependencyInErrorWhenReadinessIsRequired()
    {
        _fixture.AddPlainPlugin("some-plugin");
        _fixture.Enable("some-plugin");
        _fixture.Global.Contexts.Add(Context("some-plugin", new Mock<IEEditorPlugin>().Object, state: EEditorPluginState.Error));
        var builder = EEditorPluginBuilder.Create();
        builder.AddOptionalPluginDependency("some-plugin", null, optional => optional.AddNuget("ZLogger"));

        var context = Context("plugin");
        Assertions.AssertInt(_fixture.Global.ResolveOptionalRecipes(context, builder.PluginRecipe, requireReady: true).Count).IsEqual(0);
        Assertions.AssertInt(_fixture.Global.ResolveOptionalRecipes(context, builder.PluginRecipe).Count).IsEqual(1);
    }

    [TestCase]
    public void SwitchingLoggingMovesTheFrameworkAndEveryPluginToTheNewLoggers()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        _fixture.Enable("app");
        var switched = new List<string>();

        _fixture.Global.SwitchLogging(new GenericLoggerFactory(category => new RecordingLogger(switched, category)));
        _fixture.Context("app")!.Logger!.Log("from app");

        Assertions.AssertBool(switched.Any(l => l.EndsWith("from app"))).IsTrue();
        Assertions.AssertBool(_fixture.Log.Any(l => l.EndsWith("from app"))).IsFalse();
        // the dotnet CLI logs through the new logger as well, so it was created again
        Assertions.AssertObject(_fixture.Context("app")!.Cli).IsNotInstanceOf<RecordingCli>();
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertThrown(() => new EGlobal(_fixture.Host).SwitchLogging(new GenericLoggerFactory(null)))
            .IsInstanceOf<InvalidOperationException>();
    }

    [TestCase]
    public void OrderRetriesPutsFailedDependencyBeforeItsDependant()
    {
        var dependant = new Mock<IEEditorPlugin>();
        dependant.Setup(p => p.CreateRecipe(It.IsAny<IEEditorPluginBuilder>()))
            .Callback<IEEditorPluginBuilder>(b => b.AddPluginDependency("b_dependency"));
        var contexts = _fixture.Global.Contexts;
        contexts.Add(Context("a_dependant", dependant.Object));
        contexts.Add(Context("b_dependency", new Mock<IEEditorPlugin>().Object));
        LocalPluginAttempt Attempt(string slug) => new(Guid.NewGuid(), slug, "1.0", PersistedPluginState.Activated,
            PersistedPluginState.Failed, "recipe_operation_failed");

        var ordered = _fixture.Global.OrderRetries([Attempt("a_dependant"), Attempt("b_dependency"), Attempt("c_other")]);

        Assertions.AssertString(string.Join(",", ordered.Select(a => a.Slug))).IsEqual("b_dependency,a_dependant,c_other");
        // ordering must not leave a recipe behind; the retry itself creates it
        Assertions.AssertBool(contexts.Single(c => c.Slug == "a_dependant").IsRecipeCreated).IsFalse();
    }
}
