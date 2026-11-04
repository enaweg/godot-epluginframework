#if TOOLS
using System;
using System.Linq;
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Plugins that wait for a dependency to activate across frames, against a fake editor: disabled while they wait, or
/// whose dependency is disabled meanwhile.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class WaitingDependencyTests : IDisposable
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

    private bool Logged(string level, string text) =>
        _fixture.Log.Any(l => l.StartsWith(level + ":", StringComparison.Ordinal) && l.Contains(text));

    /// <summary>
    /// Enables <paramref name="slug"/> and leaves it enabled but not activated, like an ePlugin whose <c>_EnablePlugin</c>
    /// does not call <c>EnableEPlugin</c> (yet): plugins that need it wait for it.
    /// </summary>
    private void EnableSilently(string slug)
    {
        _fixture.Enable(slug);
        _fixture.Context(slug)!.State = EEditorPluginState.Created;
    }

    [TestCase]
    public void APluginDisabledWhileItWaitsForADependencyIsNotEnabledAgain()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base"));
        EnableSilently("base");
        _fixture.Enable("app");
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsTrue();
        var start = _fixture.Operations.Count;

        // the user turns it off in Project Settings while it waits
        _fixture.Disable("app");

        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
        // it never installed anything, so nothing is recorded for it
        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
        Assertions.AssertObject(_fixture.Store.GetShared("app")).IsNull();
        Assertions.AssertThat(_fixture.Operations.Skip(start).ToArray()).IsEmpty();

        // its dependency finally activates: nothing waits for it any more
        _fixture.Global.EnableEPlugin(_fixture.Context("base")!);
        EGlobal.RunPosted();

        Assertions.AssertObject(_fixture.State("base")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertBool(_fixture.Operations.Contains("add-nuget App.Package")).IsFalse();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void DisablingAWaitingPluginLetsTheOthersOfItsOperationFinish()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base"));
        _fixture.AddEPlugin("other", b => b.AddNuget("Other.Package"));
        EnableSilently("base");
        _fixture.Enable("app");
        // joins the operation app keeps open
        _fixture.Enable("other");
        var start = _fixture.Operations.Count;

        _fixture.Disable("app");

        Assertions.AssertObject(_fixture.State("other")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertThat(_fixture.Operations.Skip(start).ToArray()).ContainsExactly("build");
        Assertions.AssertObject(_fixture.Store.GetShared("other")!.State).IsEqual(PersistedPluginState.Activated);
        Assertions.AssertObject(_fixture.Store.GetLocal("other")).IsNull();
        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
        Assertions.AssertObject(_fixture.Store.GetShared("app")).IsNull();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void DisablingAWaitingDependencyFailsThePluginsWaitingForIt()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("lib", b => b.AddNuget("Lib.Package").AddPluginDependency("base"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("lib"));
        EnableSilently("base");
        var start = _fixture.Operations.Count;
        _fixture.Enable("lib");
        // lib is enabled and waits itself, so app waits for lib
        _fixture.Enable("app");
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Created);

        _fixture.Disable("lib");
        EGlobal.RunPosted();

        // the user turned lib off: it is neither enabled again nor recorded
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("lib")).IsFalse();
        Assertions.AssertObject(_fixture.Store.GetLocal("lib")).IsNull();
        // app cannot activate without it: it fails like for any dependency that did not activate, and is disabled
        Assertions.AssertObject(_fixture.Store.GetLocal("app")!.Reason).IsEqual("dependency_disabled");
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Detail!).Contains("Plugin dependency lib was disabled while app waited for it");
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertThat(_fixture.Operations.Skip(start).ToArray()).IsEmpty();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
        Assertions.AssertBool(Logged("log", "lib was disabled while it waited for its dependencies")).IsTrue();
    }

    [TestCase]
    public void DisablingADependencyThatPluginsWaitForFailsThemInsteadOfEnablingItAgain()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base"));
        EnableSilently("base");
        _fixture.Enable("app");

        _fixture.Disable("base");
        EGlobal.RunPosted();

        Assertions.AssertBool(_fixture.Host.Enabled.Contains("base")).IsFalse();
        Assertions.AssertObject(_fixture.State("base")).IsEqual(EEditorPluginState.Deactivated);
        Assertions.AssertObject(_fixture.Store.GetShared("base")!.State).IsEqual(PersistedPluginState.Deactivated);
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("dependency_disabled");
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void DisablingAPlainDependencyOfAWaitingPluginFailsItInsteadOfEnablingItAgain()
    {
        _fixture.AddPlainPlugin("tool");
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("tool").AddPluginDependency("base"));
        EnableSilently("base");
        // enables tool right away, then waits for base
        _fixture.Enable("app");
        _fixture.Global.InitializePluginToggles();
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("tool")).IsTrue();

        // turned off in Project Settings, which runs no framework code
        _fixture.Host.Enabled.Remove("tool");
        _fixture.Global.ObservePluginToggles();
        EGlobal.RunPosted();

        Assertions.AssertBool(_fixture.Host.Enabled.Contains("tool")).IsFalse();
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("dependency_disabled");
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }
}
#endif
