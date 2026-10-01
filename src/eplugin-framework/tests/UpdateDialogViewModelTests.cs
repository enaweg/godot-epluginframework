using System;
using System.Linq;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Internal.Update.UI;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class UpdateDialogViewModelTests
{
    [TestCase]
    public void BlockedAndFailedVersionsAreNotPreselected()
    {
        var cache = new UpdateCache(); cache.FailedUpdates["failed"] = [new("2.0", "build failed", DateTimeOffset.UtcNow)];
        var candidates = new[] { Candidate("blocked"), Candidate("failed"), Candidate("healthy") };
        var targets = candidates.Select(c => new PluginUpdateTarget(c.Slug, c.PluginName, c.InstalledVersion, c.SourceUrl, "/unused", IsBlocked: c.Slug == "blocked")).ToArray();
        var model = new UpdateDialogViewModel(candidates, targets, cache, _ => []);
        Assertions.AssertInt(model.Selected.Count).IsEqual(1);
        Assertions.AssertString(model.Selected[0].Slug).IsEqual("healthy");
        Assertions.AssertBool(model.Rows.Single(r => r.Candidate.Slug == "blocked").HasError).IsTrue();
        Assertions.AssertObject(model.Rows.Single(r => r.Candidate.Slug == "failed").Failed!).IsNotNull();
    }
    [TestCase]
    public void SourceTrustIsRequiredOnlyForSelectedRows()
    {
        var candidate = Candidate("plugin");
        var model = new UpdateDialogViewModel([candidate], [new("plugin", "Plugin", "1.0", candidate.SourceUrl, "/unused")], null,
            _ => [new("R9", FindingSeverity.Warning, "Source host changed", true)]);
        Assertions.AssertBool(model.CanApply).IsFalse();
        model.TrustChangedSource = true;
        Assertions.AssertBool(model.CanApply).IsTrue();
        model.Rows[0].Selected = false;
        Assertions.AssertBool(model.RequiresTrust).IsFalse();
        Assertions.AssertBool(model.CanApply).IsFalse();
    }
    [TestCase]
    public void MissingTargetCannotBeAppliedAndFrameworkAnnouncesRestart()
    {
        var model = new UpdateDialogViewModel([Candidate("missing"), Candidate("ePlugin")],
            [new("ePlugin", "Framework", "1.0", "https://example.org/releases", "/unused", RecordedVersion: "0.9")], null, _ => []);
        Assertions.AssertBool(model.Rows[0].HasError).IsTrue();
        Assertions.AssertBool(model.Rows[1].Findings.Any(f => f.Code == "restart")).IsTrue();
        Assertions.AssertBool(model.Rows[1].Findings.Any(f => f.Code == "recorded_version" && f.Severity == FindingSeverity.Info)).IsTrue();
    }
    private static UpdateCandidate Candidate(string slug) => new(slug, slug, "1.0.0", "2.0.0", "https://example.org/releases", null, null, new ZipPackageRef("https://example.org/plugin.zip", slug));
}
