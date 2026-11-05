#if TOOLS
using System;
using System.Linq;
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Plugins that wait for a dependency to activate across frames, against a fake editor: disabled while they wait, and
/// failed by the watchdog when their dependency never calls back.
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
    public void AnUnrelatedPluginEnabledWhileOthersWaitHasItsLicenseReviewed()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base"));
        _fixture.AddEPlugin("other", b => b.AddNuget("Other.Package"), extraConfig: "license_required=true");
        System.IO.File.WriteAllText(System.IO.Path.Combine(_fixture.Addon("other"), "LICENSE"), "Terms");
        _fixture.Host.Dialogs = true;
        EnableSilently("base");
        _fixture.Enable("app");

        _fixture.Enable("other");
        EGlobal.RunPosted();

        // other is not part of app's chain: it waits for its review instead of failing app's operation
        Assertions.AssertThat(_fixture.Global.PendingLicenseReview!.Entries.Select(e => e.Slug).ToArray()).ContainsExactly("other");
        Assertions.AssertBool(Logged("error", "has to be accepted first")).IsFalse();
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Created);
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("attempt_in_progress");
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

    [TestCase]
    public void TheWatchdogCountsOnlyTimeAndFramesWithoutProgress()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base"));
        _fixture.AddEPlugin("other", b => b.AddNuget("Other.Package"));
        EnableSilently("base");
        _fixture.Enable("app");
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var timeout = EGlobal.StallTimeout;
        const ulong frames = EGlobal.StallFrames;

        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(start, 100)).IsFalse();
        // one long frame, e.g. a build that blocked the editor, is work rather than a stall
        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(start + timeout * 2, 101)).IsFalse();
        // many quick frames are not enough either
        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(start + timeout / 2, 100 + frames * 10)).IsFalse();

        // another plugin joins the operation: progress, which starts counting anew
        var joined = start + timeout - TimeSpan.FromSeconds(1);
        _fixture.Enable("other");
        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(joined, 200)).IsFalse();
        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(start + timeout + TimeSpan.FromSeconds(1), 200 + frames)).IsFalse();

        // a dialog that asks for a decision, or a held transition, pauses it, and the count starts again afterwards
        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(joined + timeout * 3, 200 + frames * 3, paused: true)).IsFalse();
        _fixture.Global.TransitionHold = 1;
        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(joined + timeout * 4, 200 + frames * 4)).IsFalse();
        _fixture.Global.TransitionHold = 0;
        var resumed = joined + timeout * 5;
        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(resumed, 200 + frames * 5)).IsFalse();
        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(resumed + timeout - TimeSpan.FromSeconds(1), 200 + frames * 7)).IsFalse();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsTrue();
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Created);

        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(resumed + timeout, 200 + frames * 7)).IsTrue();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
        // nothing changes the watchdog without a plugin operation
        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(resumed + timeout * 3, 200 + frames * 9)).IsFalse();
    }

    [TestCase]
    public void AStalledPluginCanBeRetriedOnceItsDependencyActivates()
    {
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base"));
        EnableSilently("base");
        _fixture.Enable("app");
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _fixture.Global.CheckStalledTransition(start, 0);
        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(start + EGlobal.StallTimeout, EGlobal.StallFrames)).IsTrue();
        EGlobal.RunPosted();
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsFalse();
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("dependency_timed_out");
        Assertions.AssertBool(Logged("error", "app stopped waiting: Plugin dependency base did not activate within 30 s")).IsTrue();

        // e.g. base's author fixed its _EnablePlugin
        _fixture.Context("base")!.State = EEditorPluginState.Activated;
        _fixture.Global.RetryFailedPlugins(["app"]);

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertBool(_fixture.Operations.Contains("add-nuget App.Package")).IsTrue();
        Assertions.AssertObject(_fixture.Store.GetLocal("app")).IsNull();
        Assertions.AssertObject(_fixture.Store.GetShared("app")!.State).IsEqual(PersistedPluginState.Activated);
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void AnOperationWhoseParticipantNeverCompletesIsGivenUpOn()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        EnableSilently("app");
        // joined, but its callback never goes on: nothing waits on a stack
        Assertions.AssertBool(_fixture.Global.JoinTransition(_fixture.Context("app")!, PersistedPluginState.Activated)).IsTrue();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        _fixture.Global.CheckStalledTransition(start, 0);
        Assertions.AssertBool(_fixture.Global.CheckStalledTransition(start + EGlobal.StallTimeout, EGlobal.StallFrames)).IsTrue();

        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Reason).IsEqual("operation_timed_out");
        Assertions.AssertString(_fixture.Store.GetLocal("app")!.Detail!).IsEqual("The plugin operation made no progress for 30 s.");
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }
}
#endif
