#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Transitions: every plugin enabled or disabled together joins one attempt, recorded as a local marker before
/// anything changes and completed in the shared state file once all of them finished and the solution builds.
/// </summary>
internal sealed partial class EGlobal
{
    internal bool IsChangingPlugins => _transition is not null || _toCheckEnable.Count > 0 ||
        _toCheckDisable.Count > 0 || _transitionHold > 0;

    private sealed class PluginTransition(Guid attemptId)
    {
        public Guid AttemptId { get; } = attemptId;
        public Dictionary<PluginContext, PersistedPluginState> Participants { get; } = [];
    }

    private string RecordFailedDetail =>
        $"Recording the operation in {StateFilePath}.user failed. The Output panel shows why; check that the file is writable.";

    private string DescribeLocalState(string slug)
    {
        if (_stateStore?.IsReadOnly == true)
        {
            return "plugin state files cannot be read";
        }

        return _stateStore?.GetLocal(slug)?.Reason ?? "unknown";
    }

    internal bool JoinTransition(PluginContext context, PersistedPluginState target, bool manualRetry = false)
    {
        if (_stateStore is null)
        {
            return true; // EGlobal's existing headless tests do not initialize the editor.
        }

        if (_recipeUpdateJournal is not null)
        {
            RegisterUpdateParticipant(context.Slug, context.Metadata?.Version);
            return true;
        }

        // A plugin joins again when its operation resumes, e.g. it is disabled once its dependants are done; the
        // transition's own local marker must not block it then.
        if (_transition?.Participants.TryGetValue(context, out var joinedTarget) == true)
        {
            if (joinedTarget == target)
            {
                return true;
            }

            // A plugin that joined to stay activated, e.g. because the optional recipe it had on a plugin disabled before
            // it was removed, can still be disabled by the same transition, e.g. as a dependant of another plugin.
            if (joinedTarget != PersistedPluginState.Activated || target != PersistedPluginState.Deactivated)
            {
                context.Logger?.Error($"Plugin {context.Slug} cannot be {(target == PersistedPluginState.Activated ? "activated" : "deactivated")} by the plugin operation that is deactivating it.");
                return false;
            }

            if (!_stateStore.TryAddParticipant(_transition.AttemptId, context.Slug, context.Metadata?.Version, target))
            {
                context.State = EEditorPluginState.Error;
                FailTransition("cannot_record_participant", RecordFailedDetail);
                return false;
            }

            _transition.Participants[context] = target;
            return true;
        }

        var ownMarker = _transition is not null && _stateStore.GetLocal(context.Slug)?.AttemptId == _transition.AttemptId;
        if (_stateStore.IsReadOnly || (_stateStore.IsBlocked(context.Slug) && !manualRetry && !ownMarker))
        {
            context.Logger?.Error(
                $"Plugin {context.Slug} is blocked by local state ({DescribeLocalState(context.Slug)}). Use manual retry after recovery.");
            context.State = EEditorPluginState.Error;
            context.ErrorDetail ??= new InvalidOperationException(_stateStore.LoadProblem is { } problem
                ? $"ePlugin cannot read {problem.File}: {problem.Message}"
                : $"An earlier operation of {context.Slug} failed ({DescribeLocalState(context.Slug)}) and has to be retried or given up on first.");
            FailTransition("participant_blocked");
            return false;
        }

        var version = context.Metadata?.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            context.Logger?.Error(context.Metadata is null
                ? $"Plugin {context.Slug}: {_host.PluginDirectory(context.Slug)}/plugin.cfg cannot be read. Repair it and use manual retry."
                : $"Plugin {context.Slug} has no version in {_host.PluginDirectory(context.Slug)}/plugin.cfg. Add one (e.g. version=\"1.0.0\") and use manual retry.");
            _stateStore.TryRecordInvalid(context.Slug, version, "invalid_plugin_version", manualRetry, target);
            context.State = EEditorPluginState.Error;
            context.ErrorDetail = new InvalidOperationException(context.Metadata is null
                ? $"{_host.PluginDirectory(context.Slug)}/plugin.cfg cannot be read."
                : $"{_host.PluginDirectory(context.Slug)}/plugin.cfg has no version. Add one, e.g. version=\"1.0.0\".");
            FailTransition("invalid_plugin_version");
            return false;
        }

        if (_transition is null)
        {
            if (!_stateStore.TryBeginAttempt(context.Slug, version, target, out var id, manualRetry))
            {
                context.State = EEditorPluginState.Error;
                context.ErrorDetail = new InvalidOperationException(RecordFailedDetail);
                context.Logger?.Error($"Cannot record the start of the operation on {context.Slug}; it was not started.");
                return false;
            }

            _transition = new PluginTransition(id);
            _transitionFailed = false;
        }
        else if (!_stateStore.TryAddParticipant(_transition.AttemptId, context.Slug, version, target))
        {
            context.State = EEditorPluginState.Error;
            FailTransition("cannot_record_participant", RecordFailedDetail);
            return false;
        }

        // A new editor instance of a plugin that joined already, e.g. one a manual retry enabled again after its failed
        // activation disabled it: it takes the place of the old one, which Godot freed. The attempt has one entry per slug.
        foreach (var stale in _transition.Participants.Keys.Where(c => c.Slug == context.Slug && c != context).ToArray())
        {
            _transition.Participants.Remove(stale);
        }

        _transition.Participants.Add(context, target);
        return true;
    }

    /// <param name="detail">What went wrong, for the participants that have no error of their own, e.g. the build's errors.</param>
    /// <param name="log">The output of the build that failed, which the Needs attention dialog opens.</param>
    private void FailTransition(string reason, string? detail = null, string? log = null)
    {
        if (_recipeUpdateJournal is not null)
        {
            _recipeUpdateJournal.Failure = reason;
            _recipeUpdateJournal.Save();
            _toCheckEnable.Clear(); _toCheckDisable.Clear();
            return;
        }
        if (_transition is null)
        {
            return;
        }

        // A plugin without an error of its own failed because another one did: name that one, not the reason code.
        var participants = _transition.Participants.Keys.ToArray();
        var failed = participants.Where(p => p.ErrorDetail is not null).Select(p => p.Slug).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var participant in participants)
        {
            participant.State = EEditorPluginState.Error;
            if (participant.ErrorDetail is not null) continue;
            var text = detail ?? (failed.Length > 0
                ? $"Not completed, because {string.Join(", ", failed)} failed in the same plugin operation."
                : null);
            if (text is not null) participant.ErrorDetail = new InvalidOperationException(text);
        }

        var details = participants.Where(p => p.ErrorDetail is not null)
            .GroupBy(p => p.Slug, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().ErrorDetail!.Message, StringComparer.Ordinal);
        _stateStore?.TryFail(_transition.AttemptId, PersistedPluginState.Failed, reason, details, log);

        _toCheckEnable.Clear();
        _toCheckDisable.Clear();
        _transition = null;
        _transitionFailed = true;
    }

    /// <summary>The errors of a failed build, where they are and what to do, as plain text for the Needs attention dialog.</summary>
    private string BuildFailureDetail(int exitCode, IReadOnlyList<string> output)
    {
        var diagnosis = BuildDiagnostics.Classify(output, _host.ProjectRoot);
        if (diagnosis.Errors.Count == 0)
            return "The project did not build afterwards." + (DotnetFailure.Describe(exitCode, output) is { } described ? "\n" + described : "");
        var shown = diagnosis.Errors.Take(DotnetFailure.MaxLines).Select(e => BuildDiagnostics.Format(e with { File = RelativeToProject(e.File) }));
        var more = diagnosis.Errors.Count > DotnetFailure.MaxLines ? $"\n… and {diagnosis.Errors.Count - DotnetFailure.MaxLines} more, see the log." : "";
        var where = diagnosis.Cause switch
        {
            BuildFailureCause.ProjectCode => "\nThe errors are in the project's own code: it may use something the plugin changed or removed. Fix it, then retry.",
            BuildFailureCause.PluginCode => "\nThe errors are in plugin code. If a plugin does not build with your project's setup, report it to its author.",
            BuildFailureCause.Transient => "\n" + (DotnetFailure.Hint(exitCode, output) ?? "Another attempt may succeed: retry."),
            _ => ""
        };
        return $"The project did not build afterwards ({diagnosis.Errors.Count} error{(diagnosis.Errors.Count == 1 ? "" : "s")}):\n" +
               string.Join("\n", shown) + more + where;
    }

    private string? RelativeToProject(string? file)
    {
        if (file is null || !System.IO.Path.IsPathRooted(file)) return file;
        var relative = System.IO.Path.GetRelativePath(_host.ProjectRoot, file);
        return relative.StartsWith("..", StringComparison.Ordinal) ? file : relative.Replace('\\', '/');
    }

    /// <summary>
    /// Saves the output of a build an activation or deactivation failed in, below <c>.godot/eplugin/logs</c>, keeping the
    /// newest few; null when it cannot be written, which only costs the dialog its Open log button.
    /// </summary>
    private string? WriteBuildLog(Guid attemptId, IReadOnlyList<string> output)
    {
        const int kept = 10;
        try
        {
            var directory = System.IO.Path.Combine(_host.ProjectRoot, ".godot", "eplugin", "logs");
            System.IO.Directory.CreateDirectory(directory);
            var file = System.IO.Path.Combine(directory, $"build-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{attemptId:N}.log");
            System.IO.File.WriteAllText(file, string.Join("\n", output) + "\n");
            foreach (var old in new System.IO.DirectoryInfo(directory).GetFiles("build-*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(kept))
                old.Delete();
            return file;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _ePluginContext?.Logger?.Warn($"Cannot save the build output: {ex.Message}");
            return null;
        }
    }

    internal bool FinishTransition()
    {
        if (_recipeUpdateJournal is not null) return false;
        if (_transition is null)
        {
            return !_transitionFailed;
        }

        if (_toCheckEnable.Count > 0 || _toCheckDisable.Count > 0 || _transitionHold > 0)
        {
            return false;
        }

        var transition = _transition;
        if (transition.Participants.Any(x => x.Key.State == EEditorPluginState.Error))
        {
            FailTransition("recipe_operation_failed");
            return false;
        }

        if (transition.Participants.Any(x =>
                x.Key.State != (x.Value == PersistedPluginState.Activated
                    ? EEditorPluginState.Activated
                    : EEditorPluginState.Deactivated)))
        {
            return false; // a dependency has not completed its callback yet
        }

        if (_ePluginContext is not null)
        {
            var cli = GetOrCreateContext(_ePluginContext).Cli as ICheckedDotnetCli;
            var build = cli?.TryBuild();
            if (build is not { ExitCode: 0 })
            {
                var output = build?.Output ?? ["The dotnet CLI is unavailable."];
                FailTransition("solution_build_failed", BuildFailureDetail(build?.ExitCode ?? -1, output),
                    WriteBuildLog(transition.AttemptId, output));
                return false;
            }
        }

        var completed = transition.Participants.Select(x =>
            new SharedPluginState(x.Key.Slug,
                _stateStore?.GetShared(x.Key.Slug)?.Version ?? x.Key.Metadata?.Version ?? "",
                x.Value)).ToArray();
        if (_stateStore?.TryComplete(transition.AttemptId, completed) == false)
        {
            FailTransition("state_commit_failed", $"Saving the result in {StateFilePath} failed. The Output panel shows why; check that the file is writable and has no merge conflict.");
            return false;
        }

        _transition = null;
        return true;
    }
}
#endif
