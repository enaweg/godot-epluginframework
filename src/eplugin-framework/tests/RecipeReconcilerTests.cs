using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class RecipeReconcilerTests
{
    [TestCase]
    public void UpgradeUsesAddAndReferencesAreRemovedBeforePackageChanges()
    {
        var old = new RecipeSnapshot { Nugets = [new("Shared", "1.0", null), new("Gone", "1.0", null)],
            Projects = [new("old.csproj", null, true)], Autoloads = [new("Global", "old.gd")] };
        var next = new RecipeSnapshot { Nugets = [new("Shared", "2.0", null)], Projects = [new("new.csproj", null, true)], Autoloads = [new("Global", "new.gd")] };
        var plan = RecipeReconciler.Plan(old, next).ToList();
        Assertions.AssertBool(plan.Any(p => p.Kind == RecipeOperationKind.RemoveNuget && p.Nuget!.Name == "Shared")).IsFalse();
        Assertions.AssertBool(plan.FindIndex(p => p.Kind == RecipeOperationKind.RemoveProject) < plan.FindIndex(p => p.Kind == RecipeOperationKind.AddNuget)).IsTrue();
        var applied = old.Clone();
        foreach (var op in plan) RecipeReconciler.Record(applied, op);
        Assertions.AssertInt(RecipeReconciler.Plan(applied, next).Count).IsEqual(0);
        foreach (var op in RecipeReconciler.Plan(applied, old)) RecipeReconciler.Record(applied, op);
        Assertions.AssertInt(RecipeReconciler.Plan(applied, old).Count).IsEqual(0);
    }
    [TestCase]
    public void EqualSnapshotsAreNoopAndCyclesAreRejected()
    {
        var same = new RecipeSnapshot { Directories = ["res://addons/plugin/.src"] };
        Assertions.AssertInt(RecipeReconciler.Plan(same, same).Count).IsEqual(0);
        var rejected = false;
        try { RecipeReconciler.DependencyOrder(new Dictionary<string,RecipeSnapshot>
            { ["a"] = new() { PluginDependencies = [new("b", null)] }, ["b"] = new() { PluginDependencies = [new("a", null)] } }); }
        catch (InvalidDataException) { rejected = true; }
        Assertions.AssertBool(rejected).IsTrue();
    }
    [TestCase]
    public void BridgeKeepsOldVisibleCodeUntilNewRecipeIsReady()
    {
        var root = Path.Combine(Path.GetTempPath(), "bridge-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backup = Path.Combine(root, "backup"); var installed = Path.Combine(root, "installed");
            Directory.CreateDirectory(Path.Combine(backup, "src")); Directory.CreateDirectory(Path.Combine(installed, ".code"));
            File.WriteAllText(Path.Combine(backup, "src/Api.cs"), "old API used by game code");
            File.WriteAllText(Path.Combine(installed, ".code/Api.cs"), "new API");
            var old = new RecipeSnapshot { Directories = ["res://addons/plugin/.src"] };
            var preserved = InterimBridge.Restore("plugin", old, backup, installed);
            Assertions.AssertString(File.ReadAllText(Path.Combine(installed, "src/Api.cs"))).IsEqual("old API used by game code");
            Assertions.AssertInt(preserved.Count).IsEqual(1);
            InterimBridge.Remove(installed, preserved);
            Assertions.AssertBool(Directory.Exists(Path.Combine(installed, "src"))).IsFalse();
            Assertions.AssertBool(File.Exists(Path.Combine(installed, ".code/Api.cs"))).IsTrue();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestCase]
    public void RelativeProjectBridgePreservesMissingNeighborsWithoutOverwritingNewCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "project-bridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backup = Path.Combine(root, "backup"); var installed = Path.Combine(root, "installed");
            Directory.CreateDirectory(Path.Combine(backup, "src")); Directory.CreateDirectory(Path.Combine(installed, "src"));
            File.WriteAllText(Path.Combine(backup, "src/External.csproj"), "old project");
            File.WriteAllText(Path.Combine(backup, "src/Api.cs"), "old API");
            File.WriteAllText(Path.Combine(backup, "src/Required.cs"), "required old neighbor");
            File.WriteAllText(Path.Combine(installed, "src/Api.cs"), "new API");
            var recipe = new RecipeSnapshot { Projects = [new("addons/plugin/src/External.csproj", null, true)] };
            var preserved = InterimBridge.Restore("plugin", recipe, backup, installed);
            Assertions.AssertInt(preserved.Count).IsEqual(2);
            Assertions.AssertString(File.ReadAllText(Path.Combine(installed, "src/Api.cs"))).IsEqual("new API");
            Assertions.AssertBool(File.Exists(Path.Combine(installed, "src/Required.cs"))).IsTrue();
            InterimBridge.Remove(installed, preserved);
            Assertions.AssertBool(File.Exists(Path.Combine(installed, "src/Required.cs"))).IsFalse();
            Assertions.AssertString(File.ReadAllText(Path.Combine(installed, "src/Api.cs"))).IsEqual("new API");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestCase]
    public void RootAndAppliedOptionalsAreSerializedAsInstalledResources()
    {
        var root = new EEditorPluginRecipe { Nugets = [new("Root", "1.0", null)] };
        var optional = new EEditorPluginRecipe.OptionalPlugin("optional", ">1.0", new() { Autoloads = [new("Optional", "optional.gd")] });
        var snapshot = RecipeSnapshot.Capture(root, [optional]);
        Assertions.AssertInt(snapshot.Nugets.Count).IsEqual(1);
        Assertions.AssertInt(snapshot.Autoloads.Count).IsEqual(1);
        Assertions.AssertString(snapshot.AppliedOptionals[0].Slug).IsEqual("optional");
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot, UpdateStateStore.JsonOptions);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<RecipeSnapshot>(json, UpdateStateStore.JsonOptions)!;
        Assertions.AssertInt(RecipeReconciler.Plan(snapshot, loaded).Count).IsEqual(0);
    }
}
