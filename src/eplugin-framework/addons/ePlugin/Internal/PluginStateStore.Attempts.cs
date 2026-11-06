#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Update;
using static Enaweg.Plugin.Internal.PluginStateDocuments;

namespace Enaweg.Plugin.Internal;

/// <summary>The local attempts of the .user file: begun, joined, failed and abandoned.</summary>
internal sealed partial class PluginStateStore
{
    public bool TryBeginAttempt(string slug, string? installedVersion, PersistedPluginState targetState,
        out Guid attemptId, bool manualRetry = false)
    {
        attemptId = Guid.Empty;
        if (!ValidSlug(slug) || !IsCompleted(targetState))
        {
            return false;
        }

        var newId = Guid.NewGuid();
        if (!TryChangeLocal(next =>
            {
                if (next.TryGetValue(slug, out var blocked))
                {
                    if (!manualRetry)
                    {
                        return false;
                    }

                    foreach (var related in next.Values.Where(x => x.AttemptId == blocked.AttemptId).Select(x => x.Slug).ToArray())
                    {
                        next.Remove(related);
                    }
                }

                next[slug] = new LocalPluginAttempt(newId, slug, installedVersion, targetState,
                    PersistedPluginState.Failed, AttemptReasons.InProgress);
                return true;
            }))
        {
            return false;
        }

        attemptId = newId;
        return true;
    }

    public bool TryAddParticipant(Guid attemptId, string slug, string? installedVersion,
        PersistedPluginState targetState)
    {
        if (IsReadOnly || !ValidSlug(slug) || !IsCompleted(targetState) || !_local.Values.Any(x => x.AttemptId == attemptId))
        {
            return false;
        }

        if (_local.TryGetValue(slug, out var existing))
        {
            if (existing.AttemptId != attemptId)
            {
                return false;
            }

            if (existing.TargetState == targetState)
            {
                return true;
            }
        }

        // A participant joining again with another target, e.g. one that joined to stay activated and is then disabled,
        // keeps its installed version: that is what a recovery restores.
        return TryChangeLocal(next =>
        {
            next[slug] = existing is not null
                ? existing with { TargetState = targetState }
                : new LocalPluginAttempt(attemptId, slug, installedVersion, targetState,
                    PersistedPluginState.Failed, AttemptReasons.InProgress);
            return true;
        });
    }

    /// <param name="details">What went wrong for each participant, see <see cref="LocalPluginAttempt.Detail"/>.</param>
    /// <param name="log">The output of the build that failed, see <see cref="LocalPluginAttempt.Log"/>.</param>
    public bool TryFail(Guid attemptId, PersistedPluginState failureState, string reason,
        IReadOnlyDictionary<string, string>? details = null, string? log = null)
    {
        if (failureState is not (PersistedPluginState.Failed or PersistedPluginState.Invalid) ||
            string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return TryChangeLocal(next =>
        {
            var participants = next.Values.Where(x => x.AttemptId == attemptId).ToArray();
            foreach (var attempt in participants)
            {
                next[attempt.Slug] = attempt with
                {
                    State = failureState, Reason = reason, Detail = details?.GetValueOrDefault(attempt.Slug), Log = log
                };
            }

            return participants.Length > 0;
        });
    }

    /// <summary>A manual retry replaces the blocked attempt, so the record reflects why the retry failed.</summary>
    /// <param name="target">What the failed transition aimed for, which a retry continues.</param>
    public bool TryRecordInvalid(string slug, string? installedVersion, string reason, bool manualRetry = false,
        PersistedPluginState target = PersistedPluginState.Activated)
    {
        if (!TryBeginAttempt(slug, installedVersion, target, out var attemptId, manualRetry))
        {
            return false;
        }

        return TryFail(attemptId, PersistedPluginState.Invalid, reason);
    }

    public bool TryAbandonAttempt(Guid attemptId) =>
        TryChangeLocal(next =>
        {
            var participants = next.Values.Where(p => p.AttemptId == attemptId).Select(p => p.Slug).ToArray();
            foreach (var slug in participants) next.Remove(slug);
            return participants.Length > 0;
        });

    /// <summary>
    /// Takes a participant that changed nothing out of a running attempt, e.g. a plugin disabled while it waited for its
    /// dependencies. The others complete without it; when it was the last one, the attempt is gone.
    /// </summary>
    public bool TryRemoveParticipant(Guid attemptId, string slug) =>
        TryChangeLocal(next => next.TryGetValue(slug, out var existing) && existing.AttemptId == attemptId && next.Remove(slug));

    /// <summary>
    /// Fails some participants of a running attempt in an attempt of their own, in one write, so the others can still
    /// complete without them; e.g. plugins that waited for a dependency that never activated, and installed nothing.
    /// </summary>
    /// <param name="details">What went wrong for each of them, see <see cref="LocalPluginAttempt.Detail"/>.</param>
    public bool TrySplitFailed(Guid attemptId, IReadOnlyCollection<string> slugs, string reason,
        IReadOnlyDictionary<string, string>? details = null)
    {
        if (slugs.Count == 0 || string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        var failedId = Guid.NewGuid();
        return TryChangeLocal(next =>
        {
            if (slugs.Any(s => !next.TryGetValue(s, out var existing) || existing.AttemptId != attemptId))
            {
                return false;
            }

            foreach (var slug in slugs)
            {
                next[slug] = next[slug] with
                {
                    AttemptId = failedId, State = PersistedPluginState.Failed, Reason = reason,
                    Detail = details?.GetValueOrDefault(slug), Log = null
                };
            }

            return true;
        });
    }

    /// <summary>
    /// Changes the local attempts: <paramref name="change"/> edits a copy, which is saved and becomes the store's when it
    /// returns true. Nothing changes while the store is read-only, when <paramref name="change"/> returns false, or when
    /// the file cannot be written.
    /// </summary>
    private bool TryChangeLocal(Func<Dictionary<string, LocalPluginAttempt>, bool> change)
    {
        if (IsReadOnly)
        {
            return false;
        }

        var next = new Dictionary<string, LocalPluginAttempt>(_local, StringComparer.Ordinal);
        if (!change(next) || !SaveLocal(next))
        {
            return false;
        }

        _local = next;
        return true;
    }

    private bool SaveLocal(Dictionary<string, LocalPluginAttempt> attempts)
    {
        try
        {
            if (attempts.Count == 0)
            {
                if (File.Exists(_localPath))
                {
                    File.Delete(_localPath);
                }

                return true;
            }

            AtomicFile.Write(_localPath, WriteLocal(attempts.Values));
            return true;
        }
        catch (Exception ex)
        {
            logger?.Error($"Cannot save local plugin state at {_localPath}: {ex.Message}");
            return false;
        }
    }
}
#endif
