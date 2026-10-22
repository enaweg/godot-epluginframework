#if TOOLS
using System;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Welcomes;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Enabling and disabling plugins through <see cref="EGlobal"/> with a fake editor: dependencies, optional recipes,
/// plain plugins toggled outside the framework, licenses, failures and retries.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EGlobalLifecycleTests : IDisposable
{
    private LifecycleFixture _fixture = null!;

    [BeforeTest]
    public void Setup() => _fixture = new LifecycleFixture();

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    private string[] OperationsSince(int start) => _fixture.Operations.Skip(start).ToArray();

    [TestCase]
    public void EnablingInstallsTheRecipeOnceAndDisablingReversesIt()
    {
        var hidden = Path.Combine(_fixture.Addon("a"), ".src");
        Directory.CreateDirectory(hidden);
        _fixture.AddEPlugin("a", b => b
            .AddNuget("A.Package", "1.0.0")
            .AddProject("addons/a/A.csproj")
            .AddDirectory(hidden)
            .AddAutoload("AService", "res://addons/a/service.tscn"));

        _fixture.Enable("a");

        Assertions.AssertObject(_fixture.State("a")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertThat(_fixture.Operations).ContainsExactly(
            "add-nuget A.Package", "add-project addons/a/A.csproj", "add-reference addons/a/A.csproj", "build");
        Assertions.AssertBool(Directory.Exists(Path.Combine(_fixture.Addon("a"), "src"))).IsTrue();
        Assertions.AssertString(_fixture.Host.Autoloads["AService"]).IsEqual("res://addons/a/service.tscn");
        Assertions.AssertObject(_fixture.Store.GetShared("a")!.State).IsEqual(PersistedPluginState.Activated);
        Assertions.AssertObject(_fixture.Store.GetLocal("a")).IsNull();
        Assertions.AssertInt(_fixture.Host.Scans).IsEqual(1);

        var start = _fixture.Operations.Count;
        _fixture.Disable("a");

        Assertions.AssertObject(_fixture.State("a")).IsEqual(EEditorPluginState.Deactivated);
        Assertions.AssertThat(OperationsSince(start)).ContainsExactly(
            "remove-reference addons/a/A.csproj", "remove-project addons/a/A.csproj", "remove-nuget A.Package", "build");
        Assertions.AssertBool(Directory.Exists(hidden)).IsTrue();
        Assertions.AssertBool(_fixture.Host.Autoloads.ContainsKey("AService")).IsFalse();
        Assertions.AssertObject(_fixture.Store.GetShared("a")!.State).IsEqual(PersistedPluginState.Deactivated);
    }

    [TestCase]
    public void ALocalNuGetSourceIsMirroredIntoTheProjectsNugetConfigWhileThePluginIsEnabled()
    {
        _fixture.AddEPlugin("a", b => b.AddNuget("A.Package", "1.0.0", "res://addons/a/.nuget"));
        var config = Path.Combine(_fixture.Root, "nuget.config");

        _fixture.Enable("a");

        Assertions.AssertString(File.ReadAllText(config)).Contains("value=\"./addons/a/.nuget\"");
        Assertions.AssertString(File.ReadAllText(config)).Contains("used-by=\"a\"");

        _fixture.Disable("a");

        Assertions.AssertBool(File.Exists(config)).IsFalse();
    }

    [TestCase]
    public void EnablingAPluginEnablesItsDependenciesFirstInOneTransition()
    {
        _fixture.AddPlainPlugin("tool", "1.4");
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package").AddPluginDependency("tool", ">=1.2"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base", ">=1.0.0"));

        _fixture.Enable("app");

        Assertions.AssertBool(_fixture.Host.Enabled.IsSupersetOf(["app", "base", "tool"])).IsTrue();
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertObject(_fixture.State("base")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget Base.Package", "add-nuget App.Package", "build");
        Assertions.AssertObject(_fixture.Store.GetShared("base")!.State).IsEqual(PersistedPluginState.Activated);
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void ADependencyWithAnOlderVersionFailsTheActivation()
    {
        _fixture.AddPlainPlugin("tool", "1.1");
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("tool", ">=1.2"));

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertBool(_fixture.Operations.Any(o => o.StartsWith("add-", StringComparison.Ordinal))).IsFalse();
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("dependency_resolution_failed");
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void RetryActivatesAPluginOnceItsDependencyHasAMatchingVersion()
    {
        _fixture.AddPlainPlugin("tool", "1.1");
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("tool", ">=1.2"));
        _fixture.Enable("app");
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        var logged = _fixture.Log.Count;
        var start = _fixture.Operations.Count;

        _fixture.AddPlainPlugin("tool", "1.4");
        _fixture.Global.RetryFailedPlugins();

        var log = "log: " + string.Join(" | ", _fixture.Log.Skip(logged));
        Assertions.AssertObject(_fixture.State("app")).OverrideFailureMessage(log).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertBool(_fixture.Host.Enabled.IsSupersetOf(["app", "tool"])).IsTrue();
        // installed once, by the new editor instance
        Assertions.AssertThat(OperationsSince(start)).ContainsExactly("add-nuget App.Package", "build");
        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
        Assertions.AssertObject(_fixture.Store.GetShared("app")!.State).IsEqual(PersistedPluginState.Activated);
        Assertions.AssertBool(_fixture.Log.Skip(logged).Any(l => l.StartsWith("error", StringComparison.Ordinal))).OverrideFailureMessage(log).IsFalse();
        // one editor instance, the one Godot created when the retry enabled the plugin again
        Assertions.AssertInt(_fixture.Global.Contexts.Count(c => c.Slug == "app" && c.Plugin is not null)).IsEqual(1);
    }

    [TestCase]
    public void ADependencyThatCannotBeEnabledFailsTheActivation()
    {
        _fixture.AddEPlugin("app", b => b.AddPluginDependency("missing"));

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("missing cannot be enabled"))).IsTrue();
    }

    [TestCase]
    public void AnEPluginDependencyThatGodotRefusesFailsTheActivationInsteadOfWaiting()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base"));
        _fixture.Host.Broken.Add("base");

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
        Assertions.AssertBool(_fixture.Operations.Any(o => o.StartsWith("add-", StringComparison.Ordinal))).IsFalse();
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("base cannot be enabled"))).IsTrue();
    }

    [TestCase]
    public void DisablingADependencyDisablesItsDependantsFirst()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base"));
        _fixture.Enable("app");
        var start = _fixture.Operations.Count;

        _fixture.Disable("base");

        Assertions.AssertBool(_fixture.Host.Enabled.Overlaps(["app", "base"])).IsFalse();
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Deactivated);
        Assertions.AssertObject(_fixture.State("base")).IsEqual(EEditorPluginState.Deactivated);
        Assertions.AssertThat(OperationsSince(start)).ContainsExactly("remove-nuget App.Package", "remove-nuget Base.Package", "build");
    }

    [TestCase]
    public void DisablingAPlainDependencyInProjectSettingsDisablesItsDependants()
    {
        _fixture.AddPlainPlugin("tool");
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("tool"));
        _fixture.Enable("app");
        _fixture.Global.InitializePluginToggles();
        var start = _fixture.Operations.Count;

        // Project Settings disables it without running framework code; the framework notices on its next frame
        _fixture.Host.Enabled.Remove("tool");
        _fixture.Global.ObservePluginToggles();

        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Deactivated);
        Assertions.AssertThat(OperationsSince(start)).ContainsExactly("remove-nuget App.Package", "build");
    }

    [TestCase]
    public void OptionalRecipesFollowTheOptionalPluginInEitherOrder()
    {
        _fixture.AddPlainPlugin("extra", "2.0");
        _fixture.AddEPlugin("app", b => b
            .AddNuget("App.Package")
            .AddOptionalPluginDependency("extra", ">=1.0", optional => optional.AddNuget("App.Extra")));
        _fixture.Global.InitializePluginToggles();

        // the optional plugin is enabled after the plugin that declares it
        _fixture.Enable("app");
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget App.Package", "build");
        _fixture.Host.Enabled.Add("extra");
        _fixture.Global.ObservePluginToggles();
        Assertions.AssertThat(OperationsSince(2)).ContainsExactly("add-nuget App.Extra", "build");

        // disabling it again removes only the nested recipe
        _fixture.Host.Enabled.Remove("extra");
        _fixture.Global.ObservePluginToggles();
        Assertions.AssertThat(OperationsSince(4)).ContainsExactly("remove-nuget App.Extra", "build");
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsTrue();

        // the optional plugin is enabled first: the plugin installs the nested recipe with its own
        _fixture.Disable("app");
        _fixture.Host.Enabled.Add("extra");
        _fixture.Global.ObservePluginToggles();
        var start = _fixture.Operations.Count;
        _fixture.Enable("app");
        Assertions.AssertThat(OperationsSince(start)).ContainsExactly("add-nuget App.Package", "add-nuget App.Extra", "build");
    }

    [TestCase]
    public void APlainPluginEnabledInProjectSettingsWaitsForItsLicense()
    {
        _fixture.AddPlainPlugin("tool", extraConfig: "license_required=true");
        File.WriteAllText(Path.Combine(_fixture.Addon("tool"), "LICENSE"), "Terms");
        _fixture.Global.InitializePluginToggles();

        // without dialogs it cannot be accepted and is disabled again
        _fixture.Host.Enabled.Add("tool");
        _fixture.Global.ObservePluginToggles();
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("tool")).IsFalse();
        Assertions.AssertObject(_fixture.Global.PendingLicenseReview).IsNull();
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) &&
                                                    l.Contains("tool was not enabled: the license of tool"))).IsTrue();

        _fixture.Host.Dialogs = true;
        _fixture.Host.Enabled.Add("tool");
        _fixture.Global.ObservePluginToggles();
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("tool")).IsFalse();
        var review = _fixture.Global.PendingLicenseReview!;
        Assertions.AssertString(review.Activations.Single().Slug).IsEqual("tool");

        review.Accept("tool");
        _fixture.Global.CompleteLicenseReview(review);
        EGlobal.RunPosted();
        _fixture.Global.ObservePluginToggles();

        Assertions.AssertBool(_fixture.Host.Enabled.Contains("tool")).IsTrue();
        Assertions.AssertBool(_fixture.Store.IsLicenseAccepted("tool", "LICENSE")).IsTrue();
    }

    [TestCase]
    public void APlainPluginNothingNeedsIsToggledWithoutWork()
    {
        _fixture.AddPlainPlugin("tool");
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        _fixture.Enable("app");
        _fixture.Global.InitializePluginToggles();
        var start = _fixture.Operations.Count;

        _fixture.Host.Enabled.Add("tool");
        _fixture.Global.ObservePluginToggles();
        _fixture.Host.Enabled.Remove("tool");
        _fixture.Global.ObservePluginToggles();

        Assertions.AssertThat(OperationsSince(start)).IsEmpty();
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void OptionalRecipesOfAPlainPluginFailWhenTheStateCannotBeRecorded()
    {
        _fixture.AddPlainPlugin("extra");
        _fixture.AddEPlugin("app", b => b.AddOptionalPluginDependency("extra", null, optional => optional.AddNuget("App.Extra")));
        _fixture.Enable("app");
        _fixture.Global.InitializePluginToggles();
        var start = _fixture.Operations.Count;
        // a state file written by a newer framework is read-only
        var path = Path.Combine(_fixture.Root, "future-state.json");
        File.WriteAllText(path, "{\"schemaVersion\": 99, \"plugins\": [], \"attempts\": []}");
        var store = new PluginStateStore(path, new RecordingLogger(_fixture.Log, "store"));
        store.Load();
        _fixture.Global.StateStore = store;

        _fixture.Host.Enabled.Add("extra");
        _fixture.Global.ObservePluginToggles();

        Assertions.AssertThat(OperationsSince(start)).IsEmpty();
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) &&
                                                    l.Contains("Installing the optional recipes that extra satisfies failed"))).IsTrue();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void RemovingOptionalRecipesOfAPlainPluginFailsWhenTheStateCannotBeRecorded()
    {
        _fixture.AddPlainPlugin("extra");
        _fixture.AddEPlugin("app", b => b.AddOptionalPluginDependency("extra", null, optional => optional.AddNuget("App.Extra")));
        _fixture.Enable("extra");
        _fixture.Enable("app");
        _fixture.Global.InitializePluginToggles();
        var start = _fixture.Operations.Count;
        var path = Path.Combine(_fixture.Root, "future-state.json");
        File.WriteAllText(path, "{\"schemaVersion\": 99, \"plugins\": [], \"attempts\": []}");
        var store = new PluginStateStore(path, new RecordingLogger(_fixture.Log, "store"));
        store.Load();
        _fixture.Global.StateStore = store;

        _fixture.Host.Enabled.Remove("extra");
        _fixture.Global.ObservePluginToggles();

        Assertions.AssertThat(OperationsSince(start)).IsEmpty();
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) &&
                                                    l.Contains("Removing what needs extra failed"))).IsTrue();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void AnOptionalPluginWithAnOlderVersionIsSkipped()
    {
        _fixture.AddPlainPlugin("extra", "0.9");
        _fixture.AddEPlugin("app", b => b.AddOptionalPluginDependency("extra", ">=1.0", optional => optional.AddNuget("App.Extra")));
        _fixture.Enable("extra");

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("build");
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("extra")).IsTrue();
    }

    [TestCase]
    public void ADependencyCycleFailsInsteadOfWaitingForever()
    {
        _fixture.AddEPlugin("a", b => b.AddNuget("A.Package").AddPluginDependency("b"));
        _fixture.AddEPlugin("b", b => b.AddNuget("B.Package").AddPluginDependency("a"));
        _fixture.AddEPlugin("self", b => b.AddPluginDependency("self"));

        _fixture.Enable("a");
        _fixture.Enable("self");

        Assertions.AssertObject(_fixture.State("a")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertObject(_fixture.State("self")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertBool(_fixture.Host.Enabled.Overlaps(["a", "b", "self"])).IsFalse();
        Assertions.AssertBool(_fixture.Operations.Any(o => o.StartsWith("add-", StringComparison.Ordinal))).IsFalse();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("cycle: a -> b -> a"))).IsTrue();
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("cycle: self -> self"))).IsTrue();
    }

    [TestCase]
    public void ADependencyEnabledAgainInTheSameSessionIsNotMistakenForItsOldInstance()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddPluginDependency("base", ">=1.0"));
        _fixture.Enable("base");
        _fixture.Disable("base");

        // Godot freed the old instance; its deactivated context must not stand in for the new one
        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertObject(_fixture.State("base")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertInt(_fixture.Global.Contexts.Count(c => c.Slug == "base")).IsEqual(1);
    }

    [TestCase]
    public void ARecipeThatThrowsFailsAndRetryInstallsItOnceFixed()
    {
        _fixture.AddEPlugin("app", null);

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertString(_fixture.Context("app")!.ErrorDetail!.Message).Contains("fails");
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("recipe_creation_failed");

        _fixture.SetRecipe("app", b => b.AddNuget("App.Package"));
        _fixture.Global.RetryFailedPlugins();

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget App.Package", "build");
        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
    }

    [TestCase]
    public void AFailingRecipeOperationLeavesThePluginBlockedUntilRetry()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("Broken.Package"));
        _fixture.Cli.FailingPackages.Add("Broken.Package");

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("recipe_operation_failed");
        Assertions.AssertObject(_fixture.Store.GetShared("app")).IsNull();
        Assertions.AssertBool(_fixture.Operations.Contains("build")).IsFalse();

        _fixture.Cli.FailingPackages.Clear();
        _fixture.Global.RetryFailedPlugins();
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertObject(_fixture.Store.GetShared("app")!.State).IsEqual(PersistedPluginState.Activated);
    }

    [TestCase]
    public void APluginWithoutVersionFailsUntilPluginCfgIsRepairedAndRetried()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"), version: "");

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("invalid_plugin_version");
        Assertions.AssertThat(_fixture.Operations).IsEmpty();

        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"), version: "1.2.0");
        _fixture.Global.RetryFailedPlugins();

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertString(_fixture.Store.GetShared("app")!.Version).IsEqual("1.2.0");
        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
    }

    [TestCase]
    public void AFailedDeactivationIsRetried()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        _fixture.Enable("app");
        _fixture.Cli.BuildFails = true;

        _fixture.Disable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertObject(_fixture.Store.GetLocal("app")!.TargetState).IsEqual(PersistedPluginState.Deactivated);

        _fixture.Cli.BuildFails = false;
        var start = _fixture.Operations.Count;
        _fixture.Global.RetryFailedPlugins();

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Deactivated);
        Assertions.AssertThat(OperationsSince(start)).ContainsExactly("remove-nuget App.Package", "build");
        Assertions.AssertObject(_fixture.Store.GetShared("app")!.State).IsEqual(PersistedPluginState.Deactivated);
        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
    }

    [TestCase]
    public void RetryingADeactivationDisablesAPluginTheEditorStillHasEnabled()
    {
        // e.g. the deactivation failed and the editor restarted with the plugin still enabled
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        _fixture.Enable("app");
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt("app", "1.0.0", PersistedPluginState.Deactivated, out var id)).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryFail(id, PersistedPluginState.Failed, "recipe_operation_failed")).IsTrue();
        var start = _fixture.Operations.Count;

        _fixture.Global.RetryFailedPlugins();

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Deactivated);
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertThat(OperationsSince(start)).ContainsExactly("remove-nuget App.Package", "build");
        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
        Assertions.AssertObject(_fixture.Store.GetShared("app")!.State).IsEqual(PersistedPluginState.Deactivated);
    }

    [TestCase]
    public void RetryRecreatesAnEPluginThatHasNoEditorInstance()
    {
        // e.g. after a restart: the failed attempt is on disk, but the plugin is disabled
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt("app", "1.0.0", PersistedPluginState.Activated, out var id)).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryFail(id, PersistedPluginState.Failed, "recipe_operation_failed")).IsTrue();

        _fixture.Global.RetryFailedPlugins();

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsTrue();
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget App.Package", "build");
        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
    }

    [TestCase]
    public void RetryLeavesAttemptsItCannotRepairBlocked()
    {
        _fixture.AddPlainPlugin("tool");
        _fixture.AddEPlugin("broken", b => b.AddNuget("Broken.Package"));
        _fixture.Host.Broken.Add("broken");
        foreach (var slug in new[] { "tool", "broken" })
        {
            Assertions.AssertBool(_fixture.Store.TryBeginAttempt(slug, "1.0.0", PersistedPluginState.Activated, out var id)).IsTrue();
            Assertions.AssertBool(_fixture.Store.TryFail(id, PersistedPluginState.Failed, "recipe_operation_failed")).IsTrue();
        }

        _fixture.Global.RetryFailedPlugins();

        Assertions.AssertThat(_fixture.Operations).IsEmpty();
        Assertions.AssertObject(_fixture.Store.GetLocal("tool")).IsNotNull();
        Assertions.AssertObject(_fixture.Store.GetLocal("broken")).IsNotNull();
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("Cannot retry tool: recipe_operation_failed requires manual recovery"))).IsTrue();
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("Cannot retry broken: no editor plugin instance is available"))).IsTrue();
    }

    [TestCase]
    public void RetryingOnePluginLeavesTheOtherFailedPluginsBlocked()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        _fixture.AddEPlugin("other", b => b.AddNuget("Other.Package"));
        foreach (var slug in new[] { "app", "other" })
        {
            Assertions.AssertBool(_fixture.Store.TryBeginAttempt(slug, "1.0.0", PersistedPluginState.Activated, out var id)).IsTrue();
            Assertions.AssertBool(_fixture.Store.TryFail(id, PersistedPluginState.Failed, "recipe_operation_failed")).IsTrue();
        }

        _fixture.Global.RetryFailedPlugins(["app"]);

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget App.Package", "build");
        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
        Assertions.AssertObject(_fixture.Store.GetLocal("other")).IsNotNull();
    }

    [TestCase]
    public void ARetryCoversTheFailedDependenciesOfThePlugin()
    {
        _fixture.AddEPlugin("lib", b => b.AddNuget("Lib.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("lib"));
        _fixture.Enable("app");
        Assertions.AssertObject(_fixture.State("lib")).IsEqual(EEditorPluginState.Activated);
        foreach (var slug in new[] { "lib", "app" })
        {
            Assertions.AssertBool(_fixture.Store.TryBeginAttempt(slug, "1.0.0", PersistedPluginState.Activated, out var id)).IsTrue();
            Assertions.AssertBool(_fixture.Store.TryFail(id, PersistedPluginState.Failed, "recipe_operation_failed")).IsTrue();
        }

        Assertions.AssertThat(_fixture.Global.RetryScope(["app"], dependencies: true).Select(a => a.Slug).Order().ToArray()).ContainsExactly("app", "lib");
        Assertions.AssertThat(_fixture.Global.RetryScope(["lib"], dependencies: true).Select(a => a.Slug).ToArray()).ContainsExactly("lib");
        Assertions.AssertThat(_fixture.Global.RetryScope(["app"], dependencies: false).Select(a => a.Slug).ToArray()).ContainsExactly("app");
    }

    [TestCase]
    public void DisablingAFailedPluginUndoesItsActivationAndLeavesItDisabled()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt("app", "1.0.0", PersistedPluginState.Activated, out var id)).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryFail(id, PersistedPluginState.Failed, "recipe_operation_failed")).IsTrue();

        Assertions.AssertBool(_fixture.Global.DisableFailedPlugin("app")).IsTrue();

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Deactivated);
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertThat(_fixture.Operations).Contains("remove-nuget App.Package");
        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
        Assertions.AssertObject(_fixture.Store.GetShared("app")!.State).IsEqual(PersistedPluginState.Deactivated);
    }

    [TestCase]
    public void DisablingAPluginWithoutVersionDropsItsAttemptAndAFailedUpdateIsNotDisabled()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"), version: "");
        _fixture.Enable("app");
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("invalid_plugin_version");

        Assertions.AssertBool(_fixture.Global.DisableFailedPlugin("app")).IsTrue();

        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertThat(_fixture.Operations).IsEmpty();

        _fixture.AddEPlugin("updated", b => b.AddNuget("Updated.Package"));
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt("updated", "1.0.0", PersistedPluginState.Activated, out var id)).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryFail(id, PersistedPluginState.Failed, "update_kept_build_failed")).IsTrue();
        Assertions.AssertBool(_fixture.Global.DisableFailedPlugin("updated")).IsFalse();
        Assertions.AssertObject(_fixture.Store.GetLocal("updated")).IsNotNull();
        Assertions.AssertBool(_fixture.Global.DisableFailedPlugin("unknown")).IsFalse();
    }

    [TestCase]
    public void DisablingTheFrameworkIsRecordedInTheSharedState()
    {
        _fixture.Global.RecordFrameworkDisabled(_fixture.Framework);

        var framework = _fixture.Store.GetShared(_fixture.Framework.GetPluginSlug())!;
        Assertions.AssertObject(framework.State).IsEqual(PersistedPluginState.Deactivated);
        Assertions.AssertString(framework.Version).IsNotEmpty();
        Assertions.AssertObject(_fixture.Store.GetLocal(framework.Slug)).IsNull();
    }

    [TestCase]
    public void AFailedBuildFailsTheTransition()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        _fixture.Cli.BuildFails = true;

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Error);
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("solution_build_failed");
    }

    [TestCase]
    public void ARequiredLicenseIsAskedForBeforeAnythingIsInstalled()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"), extraConfig: "license_required=true");
        File.WriteAllText(Path.Combine(_fixture.Addon("app"), "LICENSE"), "Terms");
        _fixture.Host.Dialogs = true;

        _fixture.Enable("app");

        Assertions.AssertThat(_fixture.Operations).IsEmpty();
        var review = _fixture.Global.PendingLicenseReview!;
        Assertions.AssertString(review.Entries.Single().Slug).IsEqual("app");
        // Godot is still inside the plugin's _EnablePlugin; the framework disables it on the next frame
        EGlobal.RunPosted();
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();

        review.Accept("app");
        _fixture.Global.CompleteLicenseReview(review);
        EGlobal.RunPosted();

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget App.Package", "build");
        Assertions.AssertBool(_fixture.Store.IsLicenseAccepted("app", "LICENSE")).IsTrue();
    }

    [TestCase]
    public void ARequiredLicenseCannotBeAcceptedWithoutDialogs()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"), extraConfig: "license_required=true");
        File.WriteAllText(Path.Combine(_fixture.Addon("app"), "LICENSE"), "Terms");

        _fixture.Enable("app");
        EGlobal.RunPosted();

        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertObject(_fixture.Global.PendingLicenseReview).IsNull();
        Assertions.AssertThat(_fixture.Operations).IsEmpty();
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) && l.Contains("license"))).IsTrue();
    }

    [TestCase]
    public void WelcomePagesOfInstalledPluginsAreHandedOutOnce()
    {
        _fixture.AddEPlugin("app", b => b.SetWelcome("[b]Hello[/b]"));
        _fixture.AddPlainPlugin("quiet");
        _fixture.Host.Dialogs = true;
        _fixture.Enable("app");
        _fixture.Enable("quiet");

        var welcomes = _fixture.Global.TakeWelcomes()!;

        Assertions.AssertThat(welcomes.Select(w => w.Slug).ToArray()).ContainsExactly("app");
        _fixture.Global.CompleteWelcomes(welcomes);
        Assertions.AssertBool(_fixture.Store.IsWelcomeShown("app")).IsTrue();
    }

    [TestCase]
    public void HiddenWelcomePagesAreNeitherHandedOutNorRecorded()
    {
        _fixture.AddEPlugin("app", b => b.SetWelcome("[b]Hello[/b]"));
        _fixture.Host.Dialogs = true;
        _fixture.Enable("app");
        ProjectSettings.SetSetting(WelcomeSettings.HideKey, true);
        try
        {
            Assertions.AssertObject(_fixture.Global.TakeWelcomes()).IsNull();
            Assertions.AssertBool(_fixture.Store.IsWelcomeShown("app")).IsFalse();
        }
        finally { ProjectSettings.SetSetting(WelcomeSettings.HideKey, false); }
    }

    [TestCase]
    public void WelcomePagesComeFromTheRecipeOrTheReadmeAndAreShownAgainWhenNotSaved()
    {
        _fixture.AddEPlugin("app", b => b.SetWelcome("res://addons/app/WELCOME.md"));
        _fixture.AddPlainPlugin("tool");
        File.WriteAllText(Path.Combine(_fixture.Addon("tool"), "README.md"), "# Tool");
        _fixture.AddPlainPlugin("quiet");

        Assertions.AssertString(_fixture.Global.DescribeWelcome("app")!.Source).IsEqual("WELCOME.md");
        Assertions.AssertString(_fixture.Global.DescribeWelcome("tool")!.Text).IsEqual("# Tool");
        Assertions.AssertObject(_fixture.Global.DescribeWelcome("quiet")).IsNull();

        // a project without a state file yet cannot keep what was shown
        var store = new PluginStateStore(Path.Combine(_fixture.Root, "unsaved-state.json"), new RecordingLogger(_fixture.Log, "store"));
        store.Load();
        _fixture.Global.StateStore = store;
        _fixture.Global.CompleteWelcomes([_fixture.Global.DescribeWelcome("tool")!]);

        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("warn:", StringComparison.Ordinal) &&
                                                    l.Contains("Shown welcome pages could not be saved"))).IsTrue();
    }

    [TestCase]
    public void AFailingInitializerDoesNotKeepTheOthersFromRunning()
    {
        var ran = 0;
        _fixture.Global.AddInitializer(new Initializer(() => throw new InvalidOperationException("broken")));
        _fixture.Global.AddInitializer(new Initializer(() => ran++));

        _fixture.Global.ExecuteInitializers();

        Assertions.AssertInt(ran).IsEqual(1);
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) && l.Contains("broken"))).IsTrue();
    }

    private sealed class Initializer(Action initialize) : IInitialize
    {
        public void Initialize(IEPlugin ePlugin) => initialize();
    }
}
#endif
