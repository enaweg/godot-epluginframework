#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Dotnet;

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
            return joinedTarget == target;
        }

        var ownMarker = _transition is not null && _stateStore.GetLocal(context.Slug)?.AttemptId == _transition.AttemptId;
        if (_stateStore.IsReadOnly || (_stateStore.IsBlocked(context.Slug) && !manualRetry && !ownMarker))
        {
            context.Logger?.Error(
                $"Plugin {context.Slug} is blocked by local state ({DescribeLocalState(context.Slug)}). Use manual retry after recovery.");
            context.State = EEditorPluginState.Error;
            FailTransition("participant_blocked");
            return false;
        }

        var version = context.Metadata?.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            context.Logger?.Error(
                $"Plugin {context.Slug} has no version in plugin.cfg. Add one and use manual retry.");
            _stateStore.TryRecordInvalid(context.Slug, version, "invalid_plugin_version", manualRetry);
            context.State = EEditorPluginState.Error;
            FailTransition("invalid_plugin_version");
            return false;
        }

        if (_transition is null)
        {
            if (!_stateStore.TryBeginAttempt(context.Slug, version, target, out var id, manualRetry))
            {
                context.State = EEditorPluginState.Error;
                return false;
            }

            _transition = new PluginTransition(id);
            _transitionFailed = false;
        }
        else if (!_stateStore.TryAddParticipant(_transition.AttemptId, context.Slug, version, target))
        {
            context.State = EEditorPluginState.Error;
            FailTransition("cannot_record_participant");
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

    private void FailTransition(string reason)
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

        _stateStore?.TryFail(_transition.AttemptId, PersistedPluginState.Failed, reason);
        foreach (var participant in _transition.Participants.Keys)
        {
            participant.State = EEditorPluginState.Error;
            participant.ErrorDetail ??= new InvalidOperationException($"Plugin operation failed: {reason}");
        }

        _toCheckEnable.Clear();
        _toCheckDisable.Clear();
        _transition = null;
        _transitionFailed = true;
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
            if (cli is null || !cli.TryRebuildSolution())
            {
                FailTransition("solution_build_failed");
                return false;
            }
        }

        var completed = transition.Participants.Select(x =>
            new SharedPluginState(x.Key.Slug,
                _stateStore?.GetShared(x.Key.Slug)?.Version ?? x.Key.Metadata?.Version ?? "",
                x.Value)).ToArray();
        if (_stateStore?.TryComplete(transition.AttemptId, completed) == false)
        {
            FailTransition("state_commit_failed");
            return false;
        }

        _transition = null;
        return true;
    }
}
#endif
