using System;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Enabling and disabling plugins through <see cref="EGlobal"/> with a fake editor: dependencies, optional recipes,
/// plain plugins toggled outside the framework, licenses, failures and retries.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EGlobalLifecycleTests
{
    private LifecycleFixture _fixture = null!;

    [BeforeTest]
    public void Setup() => _fixture = new LifecycleFixture();

    [AfterTest]
    public void Cleanup() => _fixture.Dispose();

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
        Assertions.AssertBool(_fixture.Operations.Any(o => o.StartsWith("add-"))).IsFalse();
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("dependency_resolution_failed");
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
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
        Assertions.AssertBool(_fixture.Operations.Any(o => o.StartsWith("add-"))).IsFalse();
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
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:") && l.Contains("license"))).IsTrue();
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
    public void AFailingInitializerDoesNotKeepTheOthersFromRunning()
    {
        var ran = 0;
        _fixture.Global.AddInitializer(new Initializer(() => throw new InvalidOperationException("broken")));
        _fixture.Global.AddInitializer(new Initializer(() => ran++));

        _fixture.Global.ExecuteInitializers();

        Assertions.AssertInt(ran).IsEqual(1);
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:") && l.Contains("broken"))).IsTrue();
    }

    private sealed class Initializer(Action initialize) : IInitialize
    {
        public void Initialize(IEPlugin ePlugin) => initialize();
    }
}
