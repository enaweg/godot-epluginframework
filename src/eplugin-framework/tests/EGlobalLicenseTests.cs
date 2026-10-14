#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// The license reviews of <see cref="EGlobal"/> against a fake editor: dependencies, recipe licenses, automatic
/// acceptance, staged updates and plugins that are enabled already.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EGlobalLicenseTests : IDisposable
{
    private LifecycleFixture _fixture = null!;

    [BeforeTest]
    public void Setup() => _fixture = new LifecycleFixture();

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        ProjectSettings.SetSetting(LicenseSettings.AutoAcceptKey, false);
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void RequireLicense(string slug, string text = "Terms") =>
        File.WriteAllText(Path.Combine(_fixture.Addon(slug), "LICENSE"), text);

    [TestCase]
    public void DependencyLicensesAreReviewedFirstAndDecliningOneCancelsItsDependant()
    {
        _fixture.AddPlainPlugin("lib", extraConfig: "license_required=true");
        RequireLicense("lib");
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package").AddPluginDependency("lib"), extraConfig: "license_required=true");
        RequireLicense("base");
        // lib is reached through both plugins and is asked about once
        _fixture.AddEPlugin("app", b => b.AddPluginDependency("base").AddPluginDependency("lib"), extraConfig: "license_required=true");
        RequireLicense("app");
        _fixture.Host.Dialogs = true;

        _fixture.Enable("app");
        EGlobal.RunPosted();

        var review = _fixture.Global.PendingLicenseReview!;
        Assertions.AssertThat(review.Entries.Select(e => e.Slug).ToArray()).ContainsExactly("lib", "base", "app");
        Assertions.AssertThat(review.Activations.Single().Licenses.ToArray()).ContainsExactly("lib", "base", "app");
        Assertions.AssertBool(_fixture.Host.Enabled.Overlaps(["app", "base", "lib"])).IsFalse();

        review.Accept("lib");
        review.Decline("base");
        _fixture.Global.CompleteLicenseReview(review);
        EGlobal.RunPosted();

        Assertions.AssertBool(_fixture.Host.Enabled.Overlaps(["app", "base", "lib"])).IsFalse();
        Assertions.AssertThat(_fixture.Operations).IsEmpty();
        Assertions.AssertBool(_fixture.Store.IsLicenseAccepted("lib", "LICENSE")).IsTrue();
        Assertions.AssertBool(_fixture.Store.IsLicenseAccepted("base", "LICENSE")).IsFalse();
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("warn:", StringComparison.Ordinal) &&
                                                    l.Contains("was not enabled: the license of base was declined"))).IsTrue();
    }

    [TestCase]
    public void ALicenseFoundOnlyWhileADependencyIsEnabledFailsTheWaitingChain()
    {
        _fixture.AddPlainPlugin("lib", extraConfig: "license_required=true");
        RequireLicense("lib");
        // the first recipe read fails, so the license of lib is not collected before app enables base
        var reads = 0;
        _fixture.AddEPlugin("base", b =>
        {
            if (reads++ == 0) throw new InvalidOperationException("not compiled yet");
            b.AddNuget("Base.Package").AddPluginDependency("lib");
        });
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package").AddPluginDependency("base"));
        _fixture.Host.Dialogs = true;

        _fixture.Enable("app");
        EGlobal.RunPosted();

        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("warn:", StringComparison.Ordinal) &&
                                                    l.Contains("Cannot read the recipe of base") && l.Contains("not compiled yet"))).IsTrue();
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) &&
                                                    l.Contains("The license of lib has to be accepted first"))).IsTrue();
        Assertions.AssertObject(_fixture.Global.PendingLicenseReview).IsNull();
        Assertions.AssertBool(_fixture.Host.Enabled.Overlaps(["app", "lib"])).IsFalse();
        Assertions.AssertBool(_fixture.Operations.Any(o => o.StartsWith("add-", StringComparison.Ordinal))).IsFalse();
        Assertions.AssertBool(_fixture.Global.IsChangingPlugins).IsFalse();
    }

    [TestCase]
    public void ARecipeLicenseReplacesTheLicenseFileAndIsDescribedWithItsAcceptance()
    {
        _fixture.AddEPlugin("text", b => b.SetLicense("Recipe terms"), extraConfig: "license_required=true");
        RequireLicense("text", "File terms");
        _fixture.AddEPlugin("path", b => b.SetLicense("res://addons/path/TERMS.txt"));
        _fixture.AddPlainPlugin("plain");
        RequireLicense("plain", "Plain terms");

        var text = _fixture.Global.DescribeLicense("text")!;
        Assertions.AssertString(text.Entry.Source).StartsWith("text:");
        Assertions.AssertString(text.Entry.Text).IsEqual("Recipe terms");
        Assertions.AssertBool(text.Required).IsTrue();
        Assertions.AssertObject(text.Accepted).IsNull();
        Assertions.AssertString(_fixture.Global.DescribeLicense("path")!.Entry.Source).IsEqual("TERMS.txt");
        Assertions.AssertString(_fixture.Global.DescribeLicense("plain")!.Entry.Text).IsEqual("Plain terms");
        Assertions.AssertObject(_fixture.Global.DescribeLicense("missing")).IsNull();

        var review = _fixture.Global.ReviewActivationLicenses("text", "Text")!;
        Assertions.AssertString(review.Entries.Single().Source).IsEqual(text.Entry.Source);
        review.AcceptAll();
        _fixture.Global.RecordLicenses(review);

        Assertions.AssertString(_fixture.Global.DescribeLicense("text")!.Accepted!.License).IsEqual(text.Entry.Source);
        Assertions.AssertObject(_fixture.Global.ReviewActivationLicenses("text", "Text")).IsNull();
        Assertions.AssertObject(_fixture.Global.ReviewActivationLicenses("path", "Path")).IsNull();
    }

    [TestCase]
    public void AutoAcceptRecordsTheLicenseWithoutAReviewAndReportsWhenItCannotBeSaved()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"), extraConfig: "license_required=true");
        RequireLicense("app");
        ProjectSettings.SetSetting(LicenseSettings.AutoAcceptKey, true);

        _fixture.Enable("app");

        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertObject(_fixture.Global.PendingLicenseReview).IsNull();
        Assertions.AssertBool(_fixture.Store.GetLicense("app")!.Automatic).IsTrue();
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("warn:", StringComparison.Ordinal) &&
                                                    l.Contains("without showing it") && l.Contains(LicenseSettings.AutoAcceptKey))).IsTrue();

        // a project without a state file yet cannot keep the acceptance
        _fixture.AddPlainPlugin("tool", extraConfig: "license_required=true");
        RequireLicense("tool");
        var store = new PluginStateStore(Path.Combine(_fixture.Root, "unsaved-state.json"), new RecordingLogger(_fixture.Log, "store"));
        store.Load();
        _fixture.Global.StateStore = store;

        Assertions.AssertObject(_fixture.Global.ReviewActivationLicenses("tool", "Tool")).IsNull();
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) &&
                                                    l.Contains("could not be saved"))).IsTrue();
        Assertions.AssertBool(store.IsLicenseAccepted("tool", "LICENSE")).IsTrue();
    }

    [TestCase]
    public void UpdatesAskOnlyForChangedFileLicensesTheirRecipeDoesNotSet()
    {
        _fixture.AddPlainPlugin("same", extraConfig: "license_required=true");
        RequireLicense("same");
        _fixture.Store.TryRecordLicenses([new AcceptedLicense("same", "LICENSE", "1.0.0", DateTimeOffset.UtcNow, false)]);
        _fixture.AddPlainPlugin("changed", extraConfig: "license_required=true");
        _fixture.AddPlainPlugin("free");
        _fixture.AddEPlugin("recipe", b => b.SetLicense("Recipe terms"), extraConfig: "license_required=true");
        var packages = new[]
        {
            Staged("same", "license_required=true", "LICENSE"),
            Staged("changed", "license_required=true\nlicense_file=\"TERMS.txt\"", "TERMS.txt"),
            Staged("free", "", "LICENSE"),
            Staged("recipe", "license_required=true", "LICENSE")
        };

        var review = _fixture.Global.ReviewUpdateLicenses(packages)!;

        Assertions.AssertBool(review.IsUpdate).IsTrue();
        Assertions.AssertThat(review.Entries.Select(e => $"{e.Slug}:{e.Source}:{e.Version}").ToArray()).ContainsExactly("changed:TERMS.txt:2.0.0");
        Assertions.AssertThat(review.Activations.Single().Licenses.ToArray()).ContainsExactly("changed");

        ProjectSettings.SetSetting(LicenseSettings.AutoAcceptKey, true);
        Assertions.AssertObject(_fixture.Global.ReviewUpdateLicenses(packages)).IsNull();
        Assertions.AssertBool(_fixture.Store.IsLicenseAccepted("changed", "TERMS.txt")).IsTrue();
    }

    [TestCase]
    public void EnabledPluginsWithAnUnacceptedLicenseAreReviewedAndDisabledWhenDeclined()
    {
        _fixture.AddPlainPlugin("tool", extraConfig: "license_required=true");
        RequireLicense("tool");
        _fixture.AddPlainPlugin("accepted", extraConfig: "license_required=true");
        RequireLicense("accepted");
        _fixture.Store.TryRecordLicenses([new AcceptedLicense("accepted", "LICENSE", "1.0.0", DateTimeOffset.UtcNow, false)]);
        _fixture.AddEPlugin("failed", b => b.AddNuget("Failed.Package"));
        _fixture.Enable("failed");
        // a plugin that is not installed is not asked about, e.g. one in error
        _fixture.Context("failed")!.State = EEditorPluginState.Error;
        _fixture.AddEPlugin("failed", b => b.AddNuget("Failed.Package"), extraConfig: "license_required=true");
        RequireLicense("failed");
        _fixture.Host.Enabled.UnionWith(["tool", "accepted"]);

        _fixture.Global.ReviewEnabledLicenses();
        Assertions.AssertObject(_fixture.Global.PendingLicenseReview).IsNull();
        Assertions.AssertBool(_fixture.Log.Any(l => l.StartsWith("warn:", StringComparison.Ordinal) &&
                                                    l.Contains("The license of tool is not accepted"))).IsTrue();

        _fixture.Host.Dialogs = true;
        _fixture.Global.ReviewEnabledLicenses();

        var review = _fixture.Global.PendingLicenseReview!;
        Assertions.AssertThat(review.Entries.Select(e => e.Slug).ToArray()).ContainsExactly("tool");
        Assertions.AssertBool(review.Activations.Single().Enabled).IsTrue();

        review.Decline("tool");
        _fixture.Global.CompleteLicenseReview(review);
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("tool")).IsTrue(); // never inside the dialog's signal
        EGlobal.RunPosted();

        Assertions.AssertBool(_fixture.Host.Enabled.Contains("tool")).IsFalse();
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("accepted")).IsTrue();
        Assertions.AssertBool(_fixture.Log.Any(l => l.Contains("tool was disabled: the license of tool was declined"))).IsTrue();
    }

    [TestCase]
    public void EnabledLicensesAreNotReviewedWhileTheStateIsReadOnly()
    {
        _fixture.AddPlainPlugin("tool", extraConfig: "license_required=true");
        RequireLicense("tool");
        _fixture.Host.Enabled.Add("tool");
        _fixture.Host.Dialogs = true;
        var path = Path.Combine(_fixture.Root, "future-state.json");
        File.WriteAllText(path, "{\"schemaVersion\": 99, \"plugins\": [], \"attempts\": []}");
        var store = new PluginStateStore(path, new RecordingLogger(_fixture.Log, "store"));
        store.Load();
        _fixture.Global.StateStore = store;

        _fixture.Global.ReviewEnabledLicenses();

        Assertions.AssertBool(store.IsReadOnly).IsTrue();
        Assertions.AssertObject(_fixture.Global.PendingLicenseReview).IsNull();
    }

    [TestCase]
    public async Task AQueuedReviewIsHandedOutOnceAFrameLater()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"), extraConfig: "license_required=true");
        RequireLicense("app");
        _fixture.Host.Dialogs = true;
        _fixture.Enable("app");

        // Godot has not finished the toggle yet
        Assertions.AssertObject(_fixture.Global.TakeLicenseReview()).IsNull();
        var tree = (SceneTree)Engine.GetMainLoop();
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        var review = _fixture.Global.TakeLicenseReview();
        Assertions.AssertString(review!.Entries.Single().Slug).IsEqual("app");
        Assertions.AssertObject(_fixture.Global.TakeLicenseReview()).IsNull();
        Assertions.AssertObject(_fixture.Global.PendingLicenseReview).IsNull();
        EGlobal.RunPosted();
    }

    /// <summary>A staged update of version 2.0.0 whose plugin.cfg has <paramref name="config"/>.</summary>
    private ValidatedPackage Staged(string slug, string config, string licenseFile)
    {
        var dir = Path.Combine(_fixture.Root, "staging", slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.cfg"),
            $"[plugin]\n\nname=\"{slug}\"\nversion=\"2.0.0\"\nscript=\"plugin.gd\"\n{config}\n");
        File.WriteAllText(Path.Combine(dir, licenseFile), "New terms");
        if (!SemVer.TryParse("2.0.0", out var version)) throw new InvalidOperationException("2.0.0 is a version");
        var candidate = new UpdateCandidate(slug, slug, "1.0.0", "2.0.0", "https://example.org/releases", null, null,
            new ZipPackageRef("https://example.org/p.zip", slug));
        return new ValidatedPackage(candidate, dir, version, null, false, []);
    }
}
#endif
