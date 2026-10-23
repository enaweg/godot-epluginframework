#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Less common paths of the plugin lifecycle against a fake editor: dependencies in the wrong state, blocked plugins,
/// recipes that fail while a plugin is disabled, initializers and debug logging.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public partial class EGlobalEdgeCaseTests : IDisposable
{
    private LifecycleFixture _fixture = null!;

    [BeforeTest]
    public void Setup() => _fixture = new LifecycleFixture();

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        ProjectSettings.SetSetting(LoggingSettings.DebugKey, false);
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool Logged(string level, string text) =>
        _fixture.Log.Any(l => l.StartsWith(level + ":", StringComparison.Ordinal) && l.Contains(text));

    /// <summary>Leaves a failed attempt of the plugin on disk, which blocks it until a manual retry.</summary>
    private void Block(string slug, PersistedPluginState target)
    {
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt(slug, "1.0.0", target, out var id)).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryFail(id, PersistedPluginState.Failed, "recipe_operation_failed")).IsTrue();
    }

    [TestCase]
    public void AVersionedDependencyWhosePluginCfgIsGoneFailsTheActivation()
    {
        // still enabled in the project, but its folder was deleted
        _fixture.Host.Enabled.Add("tool");
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("tool", ">=1.0"));

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(Logged("warn", "Plugin tool not found!")).IsTrue();
        Assertions.AssertBool(_fixture.Operations.Any(o => o.StartsWith("add-", StringComparison.Ordinal))).IsFalse();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void ADependencyInErrorFailsItsDependantWithOrWithoutAVersion()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("pinned", b => b.AddNuget("Pinned.Package").AddPluginDependency("base", ">=1.0"));
        _fixture.AddEPlugin("loose", b => b.AddNuget("Loose.Package").AddPluginDependency("base"));
        _fixture.Enable("base");
        // e.g. a recipe operation of base failed, while Godot keeps it enabled
        _fixture.Context("base")!.State = EEditorPluginState.Error;
        var start = _fixture.Operations.Count;

        _fixture.Enable("pinned");
        _fixture.Enable("loose");

        Assertions.AssertObject(_fixture.State("pinned")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertObject(_fixture.State("loose")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertString(_fixture.Context("pinned")!.ErrorDetail!.Message).Contains("Plugin dependency base failed.");
        Assertions.AssertString(_fixture.Context("loose")!.ErrorDetail!.Message).Contains("Plugin dependency base failed.");
        Assertions.AssertBool(_fixture.Operations.Skip(start).Any(o => o.StartsWith("add-", StringComparison.Ordinal))).IsFalse();
    }

    [TestCase]
    public void ADeactivatedDependencyFailsItsDependantWithOrWithoutAVersion()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("pinned", b => b.AddNuget("Pinned.Package").AddPluginDependency("base", ">=1.0"));
        _fixture.AddEPlugin("loose", b => b.AddNuget("Loose.Package").AddPluginDependency("base"));
        _fixture.Enable("base");
        // Godot keeps it enabled, so no activation of it would resume a plugin that waits for it
        _fixture.Context("base")!.State = EEditorPluginState.Deactivated;

        _fixture.Enable("pinned");
        _fixture.Enable("loose");

        Assertions.AssertString(_fixture.Context("pinned")!.ErrorDetail!.Message).Contains("Plugin dependency base not ready but needed by pinned!");
        Assertions.AssertString(_fixture.Context("loose")!.ErrorDetail!.Message).Contains("Plugin dependency base not ready but needed by loose!");
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void APluginWaitingForADependencyThatNeverCallsBackDoesNotStopOthers()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base"));
        _fixture.AddEPlugin("other", b => b.AddNuget("Other.Package"));
        _fixture.Enable("base");
        // like an ePlugin whose _EnablePlugin does not call EnableEPlugin: enabled, but never activated
        _fixture.Context("base")!.State = EEditorPluginState.Created;
        _fixture.Enable("app");

        // resumes the waiting app, which waits again; this used to pop and push it back forever
        _fixture.Enable("other");

        Assertions.AssertObject(_fixture.State("other")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Created);
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsTrue();
    }

    [TestCase]
    public void CallbacksForAPluginThatIsDoneOrFailedChangeNothing()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        _fixture.Enable("app");
        var context = _fixture.Context("app")!;
        var start = _fixture.Operations.Count;

        _fixture.Global.EnableEPlugin(context);
        context.State = EEditorPluginState.Error;
        _fixture.Global.EnableEPlugin(context);
        _fixture.Global.DisableEPlugin(context);

        Assertions.AssertThat(_fixture.Operations.Skip(start).ToArray()).IsEmpty();
        Assertions.AssertObject(context.State).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void APluginEnabledBeforeTheFrameworkIsReadyWaitsForIt()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        _fixture.Enable("app");
        var global = new EGlobal(_fixture.Host);

        global.EnableEPlugin(_fixture.Context("app")!);

        Assertions.AssertBool(global.IsValid()).IsFalse();
        Assertions.AssertBool(global.IsChangingPlugins).IsTrue();
    }

    [TestCase]
    public void APluginDisabledWhileTheFrameworkIsNotRunningIsLeftInstalled()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddAutoload("AppService", "res://addons/app/service.tscn"));
        _fixture.Enable("app");
        var start = _fixture.Operations.Count;
        // e.g. the framework was disabled, or its Initialize failed and waits for its retry
        var global = new EGlobal(_fixture.Host);

        global.DisableEPlugin(_fixture.Context("app")!);

        Assertions.AssertThat(_fixture.Operations.Skip(start).ToArray()).IsEmpty();
        Assertions.AssertBool(_fixture.Host.Autoloads.ContainsKey("AppService")).IsTrue();
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertObject(_fixture.Store.GetShared("app")!.State).IsEqual(PersistedPluginState.Activated);
        Assertions.AssertBool(Logged("warn", "app was disabled while the ePlugin framework is not running")).IsTrue();
    }

    [TestCase]
    public void EnablingAPluginThatABlockedPluginWouldUseOptionallyFails()
    {
        _fixture.AddEPlugin("extra", b => b.AddNuget("Extra.Package"));
        _fixture.AddEPlugin("app", b => b.AddOptionalPluginDependency("extra", null, optional => optional.AddNuget("App.Extra")));
        _fixture.Enable("app");
        Block("app", PersistedPluginState.Activated);
        var start = _fixture.Operations.Count;

        _fixture.Enable("extra");

        Assertions.AssertObject(_fixture.State("extra")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertString(_fixture.Context("extra")!.ErrorDetail!.Message).Contains("Cannot record optional recipe installation for app");
        Assertions.AssertBool(Logged("error", "Plugin app is blocked by local state (recipe_operation_failed)")).IsTrue();
        Assertions.AssertBool(_fixture.Operations.Skip(start).Contains("add-nuget App.Extra")).IsFalse();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void ABlockedPluginCannotBeDisabledUntilRetried()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        _fixture.Enable("app");
        Block("app", PersistedPluginState.Deactivated);
        var start = _fixture.Operations.Count;

        _fixture.Disable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertThat(_fixture.Operations.Skip(start).ToArray()).IsEmpty();
        Assertions.AssertBool(Logged("error", "Plugin app is blocked by local state")).IsTrue();
    }

    [TestCase]
    public void ADirectoryThatCannotBeHiddenFailsTheDeactivation()
    {
        var hidden = Path.Combine(_fixture.Addon("app"), ".src");
        Directory.CreateDirectory(hidden);
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddDirectory(hidden));
        _fixture.Enable("app");
        // a hidden copy appeared next to the shown directory, so neither can be renamed onto the other
        Directory.CreateDirectory(hidden);

        _fixture.Disable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(Logged("error", "Deactivating app failed")).IsTrue();
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("deactivation_failed");
    }

    [TestCase]
    public void ARecipeThatFailsAfterAnAssemblyReloadBlocksDisablingItAndItsDependencies()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base"));
        _fixture.Enable("app");
        // an assembly reload recreated the contexts, and the new code's recipe throws
        foreach (var context in new[] { _fixture.Context("app")!, _fixture.Context("base")! })
        {
            context.IsRecipeCreated = false;
            context.Builder = EEditorPluginBuilder.Create();
        }
        _fixture.SetRecipe("app", null);
        var start = _fixture.Operations.Count;

        // base asks every installed plugin whether it depends on base
        _fixture.Disable("base");

        Assertions.AssertString(_fixture.Store.GetLocal("base")!.Reason).IsEqual("dependent_recipe_creation_failed");
        Assertions.AssertThat(_fixture.Operations.Skip(start).ToArray()).IsEmpty();

        _fixture.Disable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(Logged("error", "Creating the recipe of app failed")).IsTrue();
        Assertions.AssertThat(_fixture.Operations.Skip(start).ToArray()).IsEmpty();
    }

    [TestCase]
    public void InitializersRunOnceTheFrameworkIsReady()
    {
        ProjectSettings.SetSetting(LoggingSettings.DebugKey, true);
        var plugin = new InitializingPlugin();
        var later = new InitializingPlugin();
        try
        {
            _fixture.Global.AddContext(new PluginContext(null, plugin, null));
            Assertions.AssertInt(plugin.Calls).IsEqual(1);

            var global = new EGlobal(_fixture.Host);
            global.AddContext(new PluginContext(null, later, null));
            Assertions.AssertInt(later.Calls).IsEqual(0);
            global.Attach(_fixture.Framework, new Logging.GenericLoggerFactory(c => new RecordingLogger(_fixture.Log, c)), _fixture.Cli);
            // what the end of the framework's start does
            global.ExecuteInitializers();
            Assertions.AssertInt(later.Calls).IsEqual(1);
            // registered once the framework is ready, e.g. by a plugin enabled later: nothing would run the queue again
            global.AddInitializer(later);

            Assertions.AssertInt(later.Calls).IsEqual(2);
            Assertions.AssertBool(Logged("log", $"Initializer {typeof(InitializingPlugin).FullName} runs now, the framework is ready.")).IsTrue();
        }
        finally
        {
            plugin.Free();
            later.Free();
        }
    }

    [TestCase]
    public void DebugLoggingReportsReadyDependenciesAndUncheckableVersions()
    {
        ProjectSettings.SetSetting(LoggingSettings.DebugKey, true);
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddPluginDependency("base", ">=1.0"));

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertBool(Logged("log", "Dependency base >=1.0 ready for app.")).IsTrue();

        var log = new List<string>();
        Assertions.AssertBool(EGlobal.MatchesVersion("1.0.0", ">=not-a-version", new RecordingLogger(log, "app"))).IsFalse();
        Assertions.AssertBool(log.Single().StartsWith("warn: app: Dependency version cannot be checked:", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void AnUpdateFromThePluginDetailsAsksOnlyWhenADialogCanBeShown()
    {
        const string key = "eplugin/updates/on_build_failure";
        var original = ProjectSettings.HasSetting(key) ? ProjectSettings.GetSetting(key) : (Variant?)null;
        try
        {
            ProjectSettings.SetSetting(key, "ask");
            Assertions.AssertString(_fixture.Global.DetailsBuildFailurePolicy("app")).IsEqual("rollback");

            _fixture.Host.Dialogs = true;
            Assertions.AssertString(_fixture.Global.DetailsBuildFailurePolicy("app")).IsEqual("ask");
            // the framework itself cannot run code that does not build
            Assertions.AssertString(_fixture.Global.DetailsBuildFailurePolicy("ePlugin")).IsEqual("rollback");

            ProjectSettings.SetSetting(key, "keep");
            Assertions.AssertString(_fixture.Global.DetailsBuildFailurePolicy("app")).IsEqual("keep");
        }
        finally
        {
            if (original is { } value) ProjectSettings.SetSetting(key, value);
            else ProjectSettings.Clear(key);
        }
    }

    private sealed partial class InitializingPlugin : EditorPlugin, IInitialize
    {
        public int Calls { get; private set; }

        public void Initialize(IEPlugin ePlugin) => Calls++;
    }
}
#endif
