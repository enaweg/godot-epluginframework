#if TOOLS
using System;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>Update recipes against a fake editor, with real journals and isolated addon files.</summary>
[TestSuite]
[RequireGodotRuntime]
public class EGlobalUpdateRecipeTests : IDisposable
{
    private LifecycleFixture _fixture = null!;

    [BeforeTest]
    public void Setup() => _fixture = new LifecycleFixture();

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }

    [TestCase]
    public void PreflightRejectsHardConstraintsAndOnlyWarnsAboutInstalledOptionals()
    {
        _fixture.AddPlainPlugin("extra");
        _fixture.AddEPlugin("app", b => b.AddPluginDependency("extra", "1.0.0"));
        _fixture.AddEPlugin("watcher", b => b.AddOptionalPluginDependency("extra", "1.0.0", o => o.AddNuget("Optional.Package")));
        _fixture.Enable("extra");
        _fixture.Enable("app");
        _fixture.Enable("watcher");
        _fixture.Operations.Clear();

        var findings = _fixture.Global.UpdatePreflightFindings([Candidate("extra")]);

        Assertions.AssertInt(findings.Count).IsEqual(2);
        Assertions.AssertObject(findings.Single(f => f.Code == "R15").Severity).IsEqual(FindingSeverity.Error);
        Assertions.AssertString(findings.Single(f => f.Code == "R15").Message).Contains("app requires extra 1.0.0");
        Assertions.AssertObject(findings.Single(f => f.Code == "optional_constraint").Severity).IsEqual(FindingSeverity.Warning);
        Assertions.AssertThat(_fixture.Operations).IsEmpty();
        Assertions.AssertInt(_fixture.Global.UpdatePreflightFindings([Candidate("extra") with { NewVersion = "1.0.0" }]).Count).IsEqual(0);

        _fixture.Disable("app");
        _fixture.Disable("watcher");
        Assertions.AssertInt(_fixture.Global.UpdatePreflightFindings([Candidate("extra")]).Count).IsEqual(0);
    }

    [TestCase]
    public void PreflightAllowsInstalledNativeExtensionsOfPlugins()
    {
        // plugins with or without a recipe update their GDExtension; only the framework ships none
        _fixture.AddPlainPlugin("native");
        File.WriteAllText(Path.Combine(_fixture.Addon("native"), "native.gdextension"), "[configuration]");
        _fixture.Enable("native");
        _fixture.AddEPlugin("managed", _ => { });
        File.WriteAllText(Path.Combine(_fixture.Addon("managed"), "managed.gdextension"), "[configuration]");
        _fixture.Enable("managed");

        Assertions.AssertInt(_fixture.Global.UpdatePreflightFindings([Candidate("native"), Candidate("managed")]).Count).IsEqual(0);
    }

    [TestCase]
    public void PreparationOrdersDependenciesAndKeepsIndependentInstalledSnapshots()
    {
        var creations = 0;
        _fixture.AddEPlugin("base", b => b.AddNuget("Base.Package"));
        _fixture.AddEPlugin("app", b => { creations++; b.AddPluginDependency("base").AddNuget("App.Package"); });
        _fixture.Enable("app");
        var creationsBeforeSnapshot = creations;
        _fixture.Operations.Clear();
        var journal = Journal("app", "base");

        _fixture.Global.PrepareUpdateRecipes(journal);
        _fixture.Global.UpdatePreflightFindings([Candidate("app")]);

        Assertions.AssertThat(journal.Plugins.Select(p => p.Slug).ToArray()).ContainsExactly("base", "app");
        Assertions.AssertInt(creations).IsEqual(creationsBeforeSnapshot);
        journal.Recipes["app"].Applied.Nugets.Clear();
        Assertions.AssertString(journal.Recipes["app"].Old.Nugets.Single().Name).IsEqual("App.Package");
        Assertions.AssertInt(_fixture.Context("app")!.Builder.PluginRecipe.Nugets.Count).IsEqual(1);
        Assertions.AssertThat(_fixture.Operations).IsEmpty();
    }

    [TestCase]
    public void RebaseRetainsTheUpdatedPluginsBaselineAndRefreshesOtherEnabledPlugins()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("Old.Package"));
        _fixture.AddEPlugin("removed", b => b.AddNuget("Removed.Package"));
        _fixture.AddEPlugin("added", b => b.AddNuget("Added.Package"));
        _fixture.Enable("app");
        _fixture.Enable("removed");
        var journal = Journal("app");
        _fixture.Global.PrepareUpdateRecipes(journal);
        ReloadRecipe("app", b => b.AddNuget("New.Package"));
        _fixture.Disable("removed");
        _fixture.Enable("added");
        _fixture.Operations.Clear();

        _fixture.Global.RebaseUpdateRecipes(journal);

        Assertions.AssertString(journal.Recipes["app"].Old.Nugets.Single().Name).IsEqual("Old.Package");
        Assertions.AssertBool(journal.Recipes.ContainsKey("removed")).IsFalse();
        Assertions.AssertString(journal.Recipes["added"].Old.Nugets.Single().Name).IsEqual("Added.Package");
        Assertions.AssertBool(journal.Recipes["added"].WasEnabled).IsTrue();
        Assertions.AssertThat(_fixture.Operations).IsEmpty();
    }

    [TestCase]
    public void ReconcileReplacesResourcesPreparesTheTreeAndDoesNotBuildBetweenOperations()
    {
        var oldProject = Path.Combine(_fixture.Addon("app"), "Old.csproj");
        var newProject = Path.Combine(_fixture.Addon("app"), "New.csproj");
        var oldDirectory = Path.Combine(_fixture.Addon("app"), ".src").Replace('\\', '/');
        var newDirectory = Path.Combine(_fixture.Addon("app"), ".tools").Replace('\\', '/');
        Directory.CreateDirectory(oldDirectory);
        _fixture.AddEPlugin("app", b => b.AddNuget("Old.Package").AddProject(oldProject)
            .AddAutoload("Global", "old.gd").AddDirectory(oldDirectory));
        _fixture.Enable("app");
        var journal = BeginUpdate("app");
        Directory.CreateDirectory(newDirectory);
        File.WriteAllText(Path.Combine(newDirectory, "Code.cs"), "new code");
        var bridge = Path.Combine(_fixture.Addon("app"), "bridge");
        Directory.CreateDirectory(bridge);
        journal.Plugins[0].Preserved.Add("bridge");
        journal.Plugins[0].Uids["tools/Code.cs.uid"] = "uid://old";
        ReloadRecipe("app", b => b.AddNuget("New.Package").AddProject(newProject)
            .AddAutoload("Global", "new.gd").AddDirectory(newDirectory));
        _fixture.Operations.Clear();

        _fixture.Global.ReconcileUpdateRecipes(journal, false);

        Assertions.AssertThat(_fixture.Operations).ContainsExactly($"remove-reference {oldProject}", $"remove-project {oldProject}",
            "remove-nuget Old.Package", "add-nuget New.Package", $"add-project {newProject}", $"add-reference {newProject}");
        Assertions.AssertString(_fixture.Host.Autoloads["Global"]).IsEqual("new.gd");
        Assertions.AssertBool(Directory.Exists(oldDirectory)).IsTrue();
        Assertions.AssertBool(Directory.Exists(newDirectory)).IsFalse();
        Assertions.AssertBool(Directory.Exists(bridge)).IsFalse();
        Assertions.AssertString(File.ReadAllText(Path.Combine(_fixture.Addon("app"), "tools/Code.cs.uid"))).IsEqual("uid://old");
        var saved = UpdateJournal.Load(journal.Directory).Recipes["app"];
        Assertions.AssertBool(saved.TreePrepared).IsTrue();
        Assertions.AssertObject(saved.Pending).IsNull();
        Assertions.AssertInt(RecipeReconciler.Plan(saved.Applied, saved.Target!).Count).IsEqual(0);
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertBool(_fixture.Store.IsBlocked("app")).IsTrue();

        _fixture.Operations.Clear();
        _fixture.Global.ReconcileUpdateRecipes(journal, false);
        Assertions.AssertThat(_fixture.Operations).IsEmpty();
    }

    [TestCase]
    public void RemovingOneOwnersNugetKeepsThePackageUsedByAnotherPlugin()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("Shared.Package", "1.0"));
        _fixture.AddEPlugin("other", b => b.AddNuget("Shared.Package", "1.0"));
        _fixture.Enable("app");
        _fixture.Enable("other");
        var journal = BeginUpdate("app");
        ReloadRecipe("app", _ => { });
        _fixture.Operations.Clear();

        _fixture.Global.ReconcileUpdateRecipes(journal, false);

        Assertions.AssertThat(_fixture.Operations).IsEmpty();
        Assertions.AssertThat(journal.Recipes["app"].Applied.Nugets).IsEmpty();
        Assertions.AssertString(journal.Recipes["other"].Applied.Nugets.Single().Name).IsEqual("Shared.Package");
    }

    [TestCase]
    public void ANugetAnotherPluginNamesInOtherCaseIsKept()
    {
        // NuGet package ids ignore case
        _fixture.AddEPlugin("app", b => b.AddNuget("Shared.Package", "1.0"));
        _fixture.AddEPlugin("other", b => b.AddNuget("shared.package", "1.0"));
        _fixture.Enable("app");
        _fixture.Enable("other");
        var journal = BeginUpdate("app");
        ReloadRecipe("app", _ => { });
        _fixture.Operations.Clear();

        _fixture.Global.ReconcileUpdateRecipes(journal, false);

        Assertions.AssertThat(_fixture.Operations).IsEmpty();
    }

    [TestCase]
    public void RemovingOneOwnersProjectAndAutoloadKeepsThemForAnotherPlugin()
    {
        var shared = Path.Combine(_fixture.Root, "shared", "Lib.csproj");
        _fixture.AddEPlugin("app", b => b.AddProject(shared).AddAutoload("Shared", "res://shared/shared.gd"));
        _fixture.AddEPlugin("other", b => b.AddProject(shared).AddAutoload("Shared", "res://shared/shared.gd"));
        _fixture.Enable("app");
        _fixture.Enable("other");
        var journal = BeginUpdate("app");
        ReloadRecipe("app", _ => { });
        _fixture.Operations.Clear();

        _fixture.Global.ReconcileUpdateRecipes(journal, false);

        Assertions.AssertThat(_fixture.Operations).IsEmpty();
        Assertions.AssertString(_fixture.Host.Autoloads["Shared"]).IsEqual("res://shared/shared.gd");
        Assertions.AssertThat(journal.Recipes["app"].Applied.Projects).IsEmpty();
        Assertions.AssertThat(journal.Recipes["app"].Applied.Autoloads).IsEmpty();
    }

    [TestCase]
    public void AnUpdatedAutoloadThatAnotherPluginInstalledWithAnotherPathFailsTheUpdate()
    {
        _fixture.AddEPlugin("app", _ => { });
        _fixture.AddEPlugin("other", b => b.AddAutoload("Shared", "res://other/shared.gd"));
        _fixture.Enable("app");
        _fixture.Enable("other");
        var journal = BeginUpdate("app");
        ReloadRecipe("app", b => b.AddAutoload("Shared", "res://app/shared.gd"));

        Assertions.AssertThrown(() => _fixture.Global.ReconcileUpdateRecipes(journal, false)).IsInstanceOf<InvalidOperationException>();

        Assertions.AssertString(_fixture.Host.Autoloads["Shared"]).IsEqual("res://other/shared.gd");
    }

    [TestCase]
    public void ConflictingSharedNugetVersionsFailBeforeAnyResourceChanges()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("Shared.Package", "1.0"));
        _fixture.AddEPlugin("other", b => b.AddNuget("Shared.Package", "1.0"));
        _fixture.Enable("app");
        _fixture.Enable("other");
        var journal = BeginUpdate("app");
        ReloadRecipe("app", b => b.AddNuget("Shared.Package", "2.0"));
        _fixture.Operations.Clear();

        Assertions.AssertThrown(() => _fixture.Global.ReconcileUpdateRecipes(journal, false)).IsInstanceOf<InvalidOperationException>();

        Assertions.AssertThat(_fixture.Operations).IsEmpty();
        Assertions.AssertString(journal.Recipes["app"].Applied.Nugets.Single().Version).IsEqual("1.0");
        // The journal suppression must be released even when reconciliation fails.
        _fixture.AddEPlugin("independent", b => b.AddNuget("Independent.Package"));
        _fixture.Enable("independent");
        Assertions.AssertObject(_fixture.State("independent")).IsEqual(EEditorPluginState.Activated);
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget Independent.Package", "build");
    }

    [TestCase]
    public void FailedOperationIsPersistedAndRetriedWithoutRepeatingCompletedOperations()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("Old.Package"));
        _fixture.Enable("app");
        var journal = BeginUpdate("app");
        ReloadRecipe("app", b => b.AddNuget("New.Package"));
        _fixture.Cli.FailingPackages.Add("New.Package");
        _fixture.Operations.Clear();

        Assertions.AssertThrown(() => _fixture.Global.ReconcileUpdateRecipes(journal, false)).IsInstanceOf<InvalidOperationException>();

        var saved = UpdateJournal.Load(journal.Directory);
        Assertions.AssertObject(saved.Recipes["app"].Pending!.Kind).IsEqual(RecipeOperationKind.AddNuget);
        Assertions.AssertString(saved.Recipes["app"].Pending!.Nuget!.Name).IsEqual("New.Package");
        Assertions.AssertThat(saved.Recipes["app"].Applied.Nugets).IsEmpty();
        Assertions.AssertThat(_fixture.Operations).ContainsExactly("remove-nuget Old.Package", "add-nuget New.Package");
        _fixture.Cli.FailingPackages.Clear();
        _fixture.Operations.Clear();

        _fixture.Global.ReconcileUpdateRecipes(saved, false);

        Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget New.Package");
        Assertions.AssertObject(UpdateJournal.Load(journal.Directory).Recipes["app"].Pending).IsNull();
        Assertions.AssertString(saved.Recipes["app"].Applied.Nugets.Single().Name).IsEqual("New.Package");
    }

    [TestCase]
    public void RollbackReversesAPendingOperationAndRestoresTheOldResources()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("Old.Package").AddAutoload("Global", "old.gd"));
        _fixture.Enable("app");
        var journal = BeginUpdate("app");
        ReloadRecipe("app", b => b.AddNuget("New.Package").AddAutoload("Global", "new.gd"));
        _fixture.Cli.FailingPackages.Add("New.Package");
        Assertions.AssertThrown(() => _fixture.Global.ReconcileUpdateRecipes(journal, false)).IsInstanceOf<InvalidOperationException>();
        var saved = UpdateJournal.Load(journal.Directory);
        _fixture.Operations.Clear();

        _fixture.Global.ReconcileUpdateRecipes(saved, true);

        Assertions.AssertThat(_fixture.Operations).ContainsExactly("remove-nuget New.Package", "add-nuget Old.Package");
        Assertions.AssertString(_fixture.Host.Autoloads["Global"]).IsEqual("old.gd");
        Assertions.AssertObject(saved.Recipes["app"].Pending).IsNull();
        Assertions.AssertInt(RecipeReconciler.Plan(saved.Recipes["app"].Applied, saved.Recipes["app"].Old).Count).IsEqual(0);
        Assertions.AssertObject(_fixture.State("app")).IsEqual(EEditorPluginState.Activated);
    }

    [TestCase]
    public void ReconcileLeavesThePluginOfAnUpdateKeptBeforeReconcilingToItsOwnJournal()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        _fixture.AddEPlugin("lib", b => b.AddNuget("Old.Lib"));
        _fixture.Enable("app");
        _fixture.Enable("lib");
        _fixture.Global.InitializeUpdateJournals();
        var journal = BeginUpdate("app");
        // lib's update was kept when its interim build failed; the project builds again, so its new code is loaded
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt("lib", "1.0.0", PersistedPluginState.Activated, out var kept)).IsTrue();
        Assertions.AssertBool(_fixture.Store.TryFail(kept, PersistedPluginState.Failed, "update_kept_build_failed")).IsTrue();
        var keptJournal = new UpdateJournal
        {
            Id = "kept", Directory = Path.Combine(_fixture.Root, ".godot", "eplugin", "updates", "kept"), AttemptId = kept, BeforeReconcile = true,
            Plugins = [new() { Slug = "lib", OldVersion = "1.0.0", NewVersion = "2.0.0", IsEPlugin = true, WasEnabled = true }]
        };
        Directory.CreateDirectory(keptJournal.Directory);
        keptJournal.Save(UpdatePhase.CommittedWithErrors);
        ReloadRecipe("lib", b => b.AddNuget("New.Lib"));
        _fixture.Operations.Clear();

        _fixture.Global.ReconcileUpdateRecipes(journal, false);

        Assertions.AssertThat(_fixture.Operations).IsEmpty();
        Assertions.AssertString(journal.Recipes["lib"].Applied.Nugets.Single().Name).IsEqual("Old.Lib");
    }

    [TestCase]
    public void AnEnabledPluginMissingAfterReloadFailsBeforeChangingResources()
    {
        _fixture.AddEPlugin("app", b => b.AddNuget("App.Package"));
        _fixture.Enable("app");
        var journal = BeginUpdate("app");
        journal.Recipes["missing"] = new() { WasEnabled = true, Old = new() { Nugets = [new("Missing.Package", null, null)] } };
        _fixture.Operations.Clear();

        Assertions.AssertThrown(() => _fixture.Global.ReconcileUpdateRecipes(journal, false)).IsInstanceOf<InvalidOperationException>();
        Assertions.AssertThat(_fixture.Operations).IsEmpty();
        journal.Recipes["missing"].WasEnabled = false;
        _fixture.Global.ReconcileUpdateRecipes(journal, false);
        Assertions.AssertThat(journal.Recipes["missing"].Target!.Nugets).IsEmpty();
    }

    [TestCase]
    public void UnchangedDependantsKeepAnInstalledOptionalThatNoLongerMatches()
    {
        _fixture.AddEPlugin("extra", _ => { });
        _fixture.AddEPlugin("app", b => b.AddOptionalPluginDependency("extra", "1.0.0", o => o.AddNuget("Optional.Package")));
        _fixture.Enable("extra");
        _fixture.Enable("app");
        var journal = BeginUpdate("extra");
        _fixture.AddEPlugin("extra", _ => { }, "2.0.0");
        ReloadRecipe("extra", _ => { });
        _fixture.Operations.Clear();

        _fixture.Global.ReconcileUpdateRecipes(journal, false);

        Assertions.AssertThat(_fixture.Operations).IsEmpty();
        Assertions.AssertString(journal.Recipes["app"].Applied.Nugets.Single().Name).IsEqual("Optional.Package");
        Assertions.AssertInt(_fixture.Context("app")!.AppliedOptionalDependencies!.Count).IsEqual(1);
        Assertions.AssertString(_fixture.Context("extra")!.Metadata!.Version).IsEqual("2.0.0");
    }

    [TestCase]
    public void AnUpdatedPluginRemovesAnOptionalThatNoLongerMatches()
    {
        _fixture.AddPlainPlugin("extra");
        _fixture.AddEPlugin("app", b => b.AddOptionalPluginDependency("extra", "1.0.0", o => o.AddNuget("Optional.Package")));
        _fixture.Enable("extra");
        _fixture.Enable("app");
        var journal = BeginUpdate("app");
        _fixture.AddPlainPlugin("extra", "2.0.0");
        ReloadRecipe("app", b => b.AddOptionalPluginDependency("extra", "1.0.0", o => o.AddNuget("Optional.Package")));
        _fixture.Operations.Clear();

        _fixture.Global.ReconcileUpdateRecipes(journal, false);

        Assertions.AssertThat(_fixture.Operations).ContainsExactly("remove-nuget Optional.Package");
        Assertions.AssertThat(journal.Recipes["app"].Applied.AppliedOptionals).IsEmpty();
        Assertions.AssertThat(_fixture.Context("app")!.AppliedOptionalDependencies!).IsEmpty();
    }

    [TestCase]
    public void ANewDependencySatisfiesOptionalsAndRollbackDisablesOnlyTheNewDependency()
    {
        _fixture.AddPlainPlugin("extra");
        _fixture.AddEPlugin("app", _ => { });
        _fixture.AddEPlugin("watcher", b => b.AddOptionalPluginDependency("extra", null, o => o.AddNuget("Optional.Package")));
        _fixture.Enable("app");
        _fixture.Enable("watcher");
        var journal = BeginUpdate("app");
        ReloadRecipe("app", b => b.AddPluginDependency("extra", ">=1.0"));
        _fixture.Operations.Clear();

        _fixture.Global.ReconcileUpdateRecipes(journal, false);

        Assertions.AssertThat(_fixture.Operations).ContainsExactly("add-nuget Optional.Package");
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("extra")).IsTrue();
        Assertions.AssertString(journal.AdditionalVersions["extra"]).IsEqual("1.0.0");
        Assertions.AssertBool(journal.Recipes["extra"].WasEnabled).IsFalse();
        Assertions.AssertString(journal.Recipes["watcher"].Applied.AppliedOptionals.Single().Slug).IsEqual("extra");
        _fixture.Operations.Clear();

        _fixture.Global.ReconcileUpdateRecipes(journal, true);

        Assertions.AssertThat(_fixture.Operations).ContainsExactly("remove-nuget Optional.Package");
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("extra")).IsFalse();
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("app")).IsTrue();
    }

    [TestCase]
    public void AMissingNewDependencyLeavesADurablePendingOperation()
    {
        _fixture.AddEPlugin("app", _ => { });
        _fixture.Enable("app");
        var journal = BeginUpdate("app");
        ReloadRecipe("app", b => b.AddPluginDependency("missing", ">=1.0"));
        _fixture.Operations.Clear();

        Assertions.AssertThrown(() => _fixture.Global.ReconcileUpdateRecipes(journal, false)).IsInstanceOf<InvalidOperationException>();

        var pending = UpdateJournal.Load(journal.Directory).Recipes["app"].Pending!;
        Assertions.AssertObject(pending.Kind).IsEqual(RecipeOperationKind.EnsureDependency);
        Assertions.AssertString(pending.Dependency!.Slug).IsEqual("missing");
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("missing")).IsFalse();
        Assertions.AssertThat(_fixture.Operations).IsEmpty();
    }

    [TestCase]
    public void ANewDependencyWhoseLicenseWasNotAcceptedIsNotEnabled()
    {
        _fixture.AddPlainPlugin("extra", extraConfig: "license_required=true");
        File.WriteAllText(Path.Combine(_fixture.Addon("extra"), "LICENSE"), "Terms");
        _fixture.AddEPlugin("app", _ => { });
        _fixture.Enable("app");
        var journal = BeginUpdate("app");
        ReloadRecipe("app", b => b.AddPluginDependency("extra", ">=1.0"));
        _fixture.Operations.Clear();

        // an update cannot stop for the license dialog
        InvalidOperationException? error = null;
        try { _fixture.Global.ReconcileUpdateRecipes(journal, false); }
        catch (InvalidOperationException ex) { error = ex; }

        Assertions.AssertString(error?.Message).Contains("license of extra");
        Assertions.AssertBool(_fixture.Host.Enabled.Contains("extra")).IsFalse();
        Assertions.AssertBool(journal.AdditionalVersions.ContainsKey("extra")).IsFalse();
        Assertions.AssertThat(_fixture.Operations).IsEmpty();
    }

    [TestCase]
    public void AnIncompatibleNewDependencyIsNotEnabledAndCanBeRolledBack()
    {
        _fixture.AddPlainPlugin("extra", "0.5.0");
        _fixture.AddEPlugin("app", _ => { });
        _fixture.Enable("app");
        var journal = BeginUpdate("app");
        ReloadRecipe("app", b => b.AddPluginDependency("extra", ">=1.0"));
        _fixture.Operations.Clear();

        Assertions.AssertThrown(() => _fixture.Global.ReconcileUpdateRecipes(journal, false)).IsInstanceOf<InvalidOperationException>();

        Assertions.AssertBool(_fixture.Host.Enabled.Contains("extra")).IsFalse();
        Assertions.AssertBool(journal.AdditionalVersions.ContainsKey("extra")).IsFalse();
        Assertions.AssertObject(UpdateJournal.Load(journal.Directory).Recipes["app"].Pending!.Kind).IsEqual(RecipeOperationKind.EnsureDependency);
        _fixture.Global.ReconcileUpdateRecipes(journal, true);
        Assertions.AssertObject(journal.Recipes["app"].Pending).IsNull();
        Assertions.AssertBool(journal.Recipes["app"].Applied.PluginDependencies.Any(d => d.Slug == "extra")).IsFalse();
        Assertions.AssertThat(_fixture.Operations).IsEmpty();
    }

    private UpdateJournal Journal(params string[] slugs) => new()
    {
        Id = "test",
        Directory = Path.Combine(_fixture.Root, "updates", "test"),
        Plugins = slugs.Select(slug => new UpdatePluginJournal
        {
            Slug = slug, OldVersion = "1.0.0", NewVersion = "2.0.0", IsEPlugin = true, WasEnabled = true
        }).ToList()
    };

    private UpdateJournal BeginUpdate(string slug)
    {
        var journal = Journal(slug);
        _fixture.Global.PrepareUpdateRecipes(journal);
        Assertions.AssertBool(_fixture.Store.TryBeginAttempt(slug, "1.0.0", PersistedPluginState.Activated, out var id)).IsTrue();
        journal.AttemptId = id;
        journal.Save(UpdatePhase.InterimBuilt);
        return journal;
    }

    /// <summary>Newly loaded plugin code creates its new recipe after an assembly reload.</summary>
    private void ReloadRecipe(string slug, Action<IEEditorPluginBuilder> recipe)
    {
        _fixture.SetRecipe(slug, recipe);
        var context = _fixture.Context(slug)!;
        context.Builder = EEditorPluginBuilder.Create();
        context.IsRecipeCreated = false;
    }

    private static UpdateCandidate Candidate(string slug) => new(slug, slug, "1.0.0", "2.0.0", null, null, null,
        new LocalZipPackageRef("unused.zip"));
}
#endif
