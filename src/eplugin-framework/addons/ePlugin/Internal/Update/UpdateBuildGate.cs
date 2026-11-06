#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Dotnet;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>
/// What an update does when the project does not build with it: the values of the <c>eplugin/updates/on_build_failure</c>
/// setting, which stores them in lowercase (<see cref="UpdateFailureDecision.Parse"/>).
/// </summary>
internal enum BuildFailurePolicy { Ask, Rollback, Keep }
internal static class UpdateFailureDecision
{
    /// <summary>The policy a stored <c>on_build_failure</c> value names; a value that names none rolls back.</summary>
    public static BuildFailurePolicy Parse(string? setting) => setting switch
    {
        "ask" => BuildFailurePolicy.Ask,
        "keep" => BuildFailurePolicy.Keep,
        _ => BuildFailurePolicy.Rollback,
    };

    public static BuildFailurePolicy Choose(bool selfUpdate, BuildFailurePolicy policy, bool uiAvailable) =>
        selfUpdate || !uiAvailable ? BuildFailurePolicy.Rollback : policy;

    /// <summary>
    /// The project's <c>on_build_failure</c> setting for one transaction: <c>ask</c> asks only for an update started from
    /// a plugin's details, so installing all updates rolls back; a group of a larger batch always rolls back, because
    /// keeping a project that does not build would fail every later group too.
    /// </summary>
    /// <param name="projectCodeOnly">The build failed only in the project's own code and the plugins compile on their
    /// own: the update is kept unless the setting is <c>rollback</c>. That holds for a group, too: later groups fail with
    /// the same project errors and are kept the same way. An update from a plugin's details still asks.</param>
    public static BuildFailurePolicy Policy(BuildFailurePolicy setting, bool grouped, bool askOnBuildFailure, bool projectCodeOnly = false) =>
        projectCodeOnly && setting != BuildFailurePolicy.Rollback
            ? setting == BuildFailurePolicy.Ask && askOnBuildFailure && !grouped ? BuildFailurePolicy.Ask : BuildFailurePolicy.Keep
            : grouped ? BuildFailurePolicy.Rollback
            : setting == BuildFailurePolicy.Ask && !askOnBuildFailure ? BuildFailurePolicy.Rollback : setting;
}

/// <summary>
/// The builds of an update transaction and what their failures mean. Every build's output goes to a log in the journal
/// folder and its result into <see cref="UpdateJournal.Builds"/>. A failed interim, plain C# or final build is classified
/// (<see cref="BuildDiagnostics"/>): a transient failure is built once more, and errors only in the project's own code are
/// followed by a plugins-only build, which tells whether the update may be kept for the project to be adapted
/// (<see cref="UpdateJournal.KeptForProjectCode"/>). <see cref="Decision"/> applies the build-failure policy to it.
/// </summary>
internal sealed class UpdateBuildGate(string projectRoot, IUpdateHost host)
{
    /// <summary>Builds the project and records the build as <paramref name="name"/> in the journal.</summary>
    public BuildOutcome Build(UpdateJournal journal, string name)
    {
        var build = host.Build();
        var log = "build-" + name + ".log";
        File.WriteAllLines(Path.Combine(journal.Directory, log), build.Output);
        journal.Builds.Add(new(name, build.ExitCode, log));
        journal.Save();
        return build;
    }

    /// <summary>
    /// Builds and, when that fails, finds out where: a transient failure is built once more, and errors only in project
    /// code are followed by a plugins-only build. The journal keeps the cause, the project errors and whether the plugins
    /// compile on their own (<see cref="UpdateJournal.KeptForProjectCode"/>), which decide what becomes of the update.
    /// </summary>
    public bool Passes(UpdateJournal journal, string name)
    {
        var build = Build(journal, name);
        if (build.ExitCode == 0) return true;
        var roots = host.PluginCodeRoots(journal);
        var diagnosis = BuildDiagnostics.Classify(build.Output, projectRoot, roots);
        if (diagnosis.Cause == BuildFailureCause.Transient)
        {
            host.Log("The build failed for a reason that may pass on another attempt; building again.");
            build = Build(journal, name + "-again");
            if (build.ExitCode == 0) return true;
            diagnosis = BuildDiagnostics.Classify(build.Output, projectRoot, roots);
        }
        journal.FailureCause = diagnosis.Cause;
        journal.ProjectErrors = diagnosis.ProjectErrors.ToList();
        journal.PluginsBuildAlone = diagnosis.Cause == BuildFailureCause.ProjectCode && MayKeepForProjectCode(journal) ? BuildPluginsOnly(journal, name, roots) : null;
        journal.Save();
        return false;
    }

    /// <summary>What the build-failure policy makes of the journal's failed build: roll back, ask or keep.</summary>
    public BuildFailurePolicy Decision(UpdateJournal journal) =>
        UpdateFailureDecision.Choose(journal.Plugins.Any(p => p.Slug == Framework.Slug),
            UpdateFailureDecision.Policy(host.BuildFailurePolicy, journal.Group is not null, journal.AskOnBuildFailure, journal.KeptForProjectCode), host.UiAvailable);

    /// <summary>
    /// Records a failed Retry build of an update kept with build errors: while the project is being adapted, the journal
    /// shows the project errors that are left.
    /// </summary>
    public void RetryFailed(UpdateJournal journal, BuildOutcome retry)
    {
        var diagnosis = BuildDiagnostics.Classify(retry.Output, projectRoot, host.PluginCodeRoots(journal));
        if (journal.KeptForProjectCode && diagnosis.Cause == BuildFailureCause.ProjectCode)
        {
            journal.ProjectErrors = diagnosis.ProjectErrors.ToList();
            journal.Failure = ProjectCodeFailure(journal);
            journal.Save();
        }
    }

    /// <summary>The failure of an update kept because only the project's own code does not compile with it.</summary>
    public static string ProjectCodeFailure(UpdateJournal journal)
    {
        var count = journal.ProjectErrors.Count;
        var errors = count >= BuildDiagnostics.MaxProjectErrors ? $"{count}+ errors" : count == 1 ? "1 error" : $"{count} errors";
        return $"The project's own code does not compile with the updated plugins ({errors}); the plugins compile on their own. " +
               "Adapt the project's code, then use Retry under Needs attention in the ePlugin Manager to finish the update";
    }

    /// <summary>
    /// Self-updates always roll back (a kept framework cannot load its new code, and early recovery would restore it),
    /// so do headless runs; the plugins-only build is skipped when the settings would roll back anyway.
    /// </summary>
    private bool MayKeepForProjectCode(UpdateJournal journal) =>
        host.KeepOnProjectErrors && host.UiAvailable && host.BuildFailurePolicy != BuildFailurePolicy.Rollback && journal.Plugins.All(p => p.Slug != Framework.Slug);

    private bool BuildPluginsOnly(UpdateJournal journal, string name, IReadOnlyList<string> roots)
    {
        name += "-plugins-only";
        host.Log("The build failed only in the project's own code; building the plugins on their own.");
        var work = Path.Combine(journal.Directory, name);
        var log = "build-" + name + ".log";
        BuildOutcome build;
        try { build = host.BuildPluginsOnly(new(work, [Path.Combine(projectRoot, "addons"), ..roots], Path.Combine(journal.Directory, "build-" + name + ".errors.log"))); }
        catch (Exception ex) { build = new(1, ["The plugins-only build could not run: " + ex.Message]); }
        finally
        {
            try { AddonFiles.DeleteTree(work); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { host.Log($"Cannot remove {work}: {ex.Message}"); }
        }
        File.WriteAllLines(Path.Combine(journal.Directory, log), build.Output);
        journal.Builds.Add(new(name, build.ExitCode, log));
        journal.Save();
        host.Log(build.ExitCode == 0 ? "The plugins compile on their own; the project's code has to be adapted to the update."
            : "The plugins do not compile on their own either.");
        return build.ExitCode == 0;
    }
}
#endif
