#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Logging;
using static Enaweg.Plugin.Internal.PluginStateDocuments;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// A plugin state as the files record it. Its values are written by name and can never change or be added to: see the
/// remarks of <see cref="PluginStateStore"/>.
/// </summary>
internal enum PersistedPluginState
{
    Activated,
    Deactivated,
    Failed,
    Invalid
}

/// <param name="Missing">
/// Recorded as activated, but its folder was gone at an editor start; forgotten at the next start if it still is. Left
/// out of the file while false.
/// </param>
internal sealed record SharedPluginState(string Slug, string Version, PersistedPluginState State, bool Missing = false);

/// <summary>A plugin license accepted for the project.</summary>
/// <param name="License">
/// What was accepted: the license file relative to the plugin directory, a res:// path set by the recipe, or
/// <c>text:</c> and a hash of a license text set by the recipe. A license counts as accepted while this stays the same.
/// </param>
/// <param name="Version">The plugin version whose license was accepted.</param>
/// <param name="Automatic">Accepted by <c>eplugin/licenses/auto_accept</c>, without being shown.</param>
internal sealed record AcceptedLicense(string Slug, string License, string Version, DateTimeOffset AcceptedUtc,
    bool Automatic);

/// <summary>A plugin whose welcome page was shown for the project. It is not shown again, also not after updates.</summary>
/// <param name="Version">The plugin version whose welcome page was shown.</param>
internal sealed record ShownWelcome(string Slug, string Version, DateTimeOffset ShownUtc);

/// <param name="Detail">
/// What went wrong for this plugin, e.g. the dependency that is missing or the errors dotnet reported, so the Needs
/// attention dialog explains it also after an editor restart. Optional: older files have none.
/// </param>
/// <param name="Log">The output of the build that failed, when the attempt failed there.</param>
internal sealed record LocalPluginAttempt(
    Guid AttemptId,
    string Slug,
    string? InstalledVersion,
    PersistedPluginState TargetState,
    PersistedPluginState State,
    string Reason,
    string? Detail = null,
    string? Log = null);

/// <summary>
/// The committed file contains only completed states, the accepted plugin licenses and the shown welcome pages. The
/// adjacent .user file is a
/// local journal that blocks an automatic retry when a transition fails or an assembly reload interrupts it.
/// </summary>
/// <remarks>
/// <c>licenses</c> and <c>welcomes</c> are left out while they are empty, and an entry's <c>missing</c> while it is false.
/// Every field added later is optional, so files of older versions are read without losing state. Fields this version
/// does not know, written by a newer one, are skipped when reading and not written again, so they are lost after a
/// downgrade; only another <c>schemaVersion</c> makes the files read-only. The JSON form is in
/// <see cref="PluginStateDocuments"/>.
/// <para>
/// The values of <see cref="PersistedPluginState"/> are frozen: they are written by name, and a reader refuses a name it
/// does not know (<c>JsonStringEnumConverter</c> with <c>allowIntegerValues: false</c>), so an older version could no
/// longer read a file with a new value at all. A new state goes into a new optional field instead.
/// </para>
/// <para>
/// Plugins in a subfolder of <c>res://addons</c> have a slug with <c>/</c>, which versions before them refused in every
/// list, so that the whole file could no longer be read. Their entries are written to lists of their own
/// (<c>nestedPlugins</c>, <c>nestedLicenses</c>, <c>nestedWelcomes</c>, and <c>nestedAttempts</c> in the local file),
/// which older versions skip; reading takes entries of either list.
/// </para>
/// <para>
/// The shared file is committed and edited by everyone on the project, so it changes only with its content:
/// <c>lastCompletedAttemptId</c> names the last acknowledged update, which recovery needs; enabling and disabling plugins
/// leave it alone, and an attempt that changes no entry does not write the file at all.
/// </para>
/// </remarks>
internal sealed partial class PluginStateStore(string sharedPath, ILogger? logger)
{
    private readonly string _sharedPath = sharedPath;
    private readonly string _localPath = sharedPath + ".user";
    private Dictionary<string, SharedPluginState> _shared = new(StringComparer.Ordinal);
    private Dictionary<string, LocalPluginAttempt> _local = new(StringComparer.Ordinal);
    private Dictionary<string, AcceptedLicense> _licenses = new(StringComparer.Ordinal);
    private Dictionary<string, ShownWelcome> _welcomes = new(StringComparer.Ordinal);
    // Licenses and welcome pages recorded while they could not be saved. They count for this session, also after the file
    // is read again (a git pull), unless it holds a newer entry, and the next write that succeeds saves them.
    private readonly Dictionary<string, AcceptedLicense> _unsavedLicenses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShownWelcome> _unsavedWelcomes = new(StringComparer.Ordinal);
    private byte[]? _sharedBytes;
    private Guid? _lastCompletedAttemptId;

    public Guid? LastCompletedAttemptId => _lastCompletedAttemptId;
    public Guid? GetAttemptId(string slug) => GetLocal(slug)?.AttemptId;
    public bool IsReadOnly { get; private set; }

    /// <summary>Why <see cref="Load"/> failed and left the store read-only; null when it was read.</summary>
    public StateFileProblem? LoadProblem { get; private set; }
    public bool HasSharedFile => _sharedBytes is not null;
    public IReadOnlyCollection<SharedPluginState> SharedStates => _shared.Values;
    public IReadOnlyCollection<LocalPluginAttempt> LocalAttempts => _local.Values;

    public SharedPluginState? GetShared(string slug) => _shared.GetValueOrDefault(slug);
    public LocalPluginAttempt? GetLocal(string slug) => _local.GetValueOrDefault(slug);
    public bool IsBlocked(string slug) => _local.ContainsKey(slug);
    public AcceptedLicense? GetLicense(string slug) => _licenses.GetValueOrDefault(slug);
    public ShownWelcome? GetWelcome(string slug) => _welcomes.GetValueOrDefault(slug);
    public bool IsWelcomeShown(string slug) => _welcomes.ContainsKey(slug);

    /// <summary>Whether the license of <paramref name="slug"/> was accepted as <paramref name="license"/>.</summary>
    public bool IsLicenseAccepted(string slug, string license) =>
        GetLicense(slug) is { } accepted && string.Equals(accepted.License, license, StringComparison.Ordinal);

    /// <summary>
    /// Records accepted licenses in the shared file. They count as accepted for this session even when they cannot be
    /// saved, e.g. while the files are read-only, so a plugin is not asked about again right after it was accepted.
    /// </summary>
    public bool TryRecordLicenses(IEnumerable<AcceptedLicense> accepted) =>
        TryRecord(accepted.Where(ValidLicense), l => l.Slug, l => l.AcceptedUtc, ref _licenses, _unsavedLicenses,
            next => SaveShared(_shared, _lastCompletedAttemptId, licenses: next));

    /// <summary>
    /// Records shown welcome pages in the shared file. They count as shown for this session even when they cannot be
    /// saved, so a page is not shown twice in a row.
    /// </summary>
    public bool TryRecordWelcomes(IEnumerable<ShownWelcome> shown) =>
        TryRecord(shown.Where(w => ValidSlug(w.Slug)), w => w.Slug, w => w.ShownUtc, ref _welcomes, _unsavedWelcomes,
            next => SaveShared(_shared, _lastCompletedAttemptId, welcomes: next));

    /// <summary>
    /// Records <paramref name="entries"/> in <paramref name="recorded"/>, the store's licenses or welcome pages, through
    /// <paramref name="save"/>. When they cannot be saved, they are kept in <paramref name="unsaved"/> and still added.
    /// </summary>
    /// <param name="recorded">
    /// The store's field, by reference: a failed write may have read the file again first and replaced it, so the entries
    /// are added to what the store holds afterwards.
    /// </param>
    private bool TryRecord<T>(IEnumerable<T> entries, Func<T, string> slug, Func<T, DateTimeOffset> time,
        ref Dictionary<string, T> recorded, Dictionary<string, T> unsaved, Func<Dictionary<string, T>, bool> save)
    {
        var valid = entries.ToArray();
        var next = new Dictionary<string, T>(recorded, StringComparer.Ordinal);
        foreach (var entry in valid)
        {
            next[slug(entry)] = entry;
        }

        // without a shared file the baseline has not been created yet, and must not be replaced by this entry
        if (!IsReadOnly && HasSharedFile && save(next))
        {
            return true;
        }

        foreach (var entry in valid) unsaved[slug(entry)] = entry;
        recorded = WithUnsaved(recorded, unsaved, time);
        return false;
    }

    /// <summary>
    /// <paramref name="entries"/> with the <paramref name="unsaved"/> ones, except where it holds a newer entry of the same
    /// plugin, which <paramref name="unsaved"/> then no longer keeps.
    /// </summary>
    private static Dictionary<string, T> WithUnsaved<T>(Dictionary<string, T> entries, Dictionary<string, T> unsaved, Func<T, DateTimeOffset> time)
    {
        var result = new Dictionary<string, T>(entries, StringComparer.Ordinal);
        foreach (var (slug, entry) in unsaved.ToArray())
        {
            if (result.TryGetValue(slug, out var saved) && !Equals(saved, entry) && time(saved) >= time(entry)) unsaved.Remove(slug);
            else result[slug] = entry;
        }
        return result;
    }

    public bool Load()
    {
        // Left-over temporary files of an interrupted write are only clutter: one that cannot be deleted (e.g. locked on
        // Windows) must not make the state unreadable.
        CleanTemporaryFiles(_sharedPath);
        CleanTemporaryFiles(_localPath);
        // the file being read, for the error
        var file = _sharedPath;
        try
        {
            // kept only once both files are valid: a failed read must not mix the new file with the state read before
            var sharedBytes = File.Exists(_sharedPath) ? File.ReadAllBytes(_sharedPath) : null;
            var shared = sharedBytes is null ? EmptyShared() : ReadShared(sharedBytes);
            file = _localPath;
            var localBytes = File.Exists(_localPath) ? File.ReadAllBytes(_localPath) : null;
            var local = localBytes is null ? EmptyLocal() : ReadLocal(localBytes);
            _sharedBytes = sharedBytes;
            UseShared(shared);
            _local = local.Attempts!.ToDictionary(x => x.Slug!, x => x.ToAttempt(), StringComparer.Ordinal);
            IsReadOnly = false;
            LoadProblem = null;

            if (_lastCompletedAttemptId is Guid completed)
            {
                var stale = _local.Values.Where(x => x.AttemptId == completed).Select(x => x.Slug).ToArray();
                if (stale.Length > 0)
                {
                    foreach (var slug in stale)
                    {
                        _local.Remove(slug);
                    }

                    if (!SaveLocal(_local))
                    {
                        logger?.Warn($"Could not remove a completed local attempt from {_localPath}; it will be ignored.");
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            IsReadOnly = true;
            LoadProblem = StateFileProblem.Describe(file, file == _sharedPath, ex);
            logger?.Error($"Cannot read the plugin state file {LoadProblem.File}: {LoadProblem.Message} {LoadProblem.Hint} " +
                          "ePlugin changes no plugin until then; the files were left as they are.");
            return false;
        }
    }

    /// <summary>Takes the entries of a read shared document as this store's own.</summary>
    private void UseShared(SharedDocument shared)
    {
        _shared = shared.Plugins!.ToDictionary(x => x.Slug!, x => x.ToState(), StringComparer.Ordinal);
        _licenses = (shared.Licenses ?? []).ToDictionary(x => x.Slug!, x => x.ToLicense(), StringComparer.Ordinal);
        _welcomes = (shared.Welcomes ?? []).ToDictionary(x => x.Slug!, x => x.ToWelcome(), StringComparer.Ordinal);
        _licenses = WithUnsaved(_licenses, _unsavedLicenses, l => l.AcceptedUtc);
        _welcomes = WithUnsaved(_welcomes, _unsavedWelcomes, w => w.ShownUtc);
        _lastCompletedAttemptId = shared.LastCompletedAttemptId;
    }

    /// <summary>Creates the one-time baseline only when no shared file exists.</summary>
    public bool TryCreateBaseline(IEnumerable<SharedPluginState> states)
    {
        if (IsReadOnly || HasSharedFile)
        {
            return false;
        }

        var next = new Dictionary<string, SharedPluginState>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            // a plugin with a local attempt gets its entry when the attempt completes
            if (IsBlocked(state.Slug))
            {
                continue;
            }

            if (!ValidShared(state) || !next.TryAdd(state.Slug, state))
            {
                logger?.Error($"Cannot create plugin state baseline for {state.Slug}: invalid or duplicate entry.");
                return false;
            }
        }

        return SaveShared(next, null);
    }

    /// <summary>Commits every participant together; a failed write leaves .user in place.</summary>
    public bool TryComplete(Guid attemptId, IEnumerable<SharedPluginState> completedStates) =>
        Complete(attemptId, completedStates, acknowledgeVersions: false);

    public bool TryAcknowledgeVersions(Guid attemptId, IReadOnlyDictionary<string, string> versions)
    {
        var participants = _local.Values.Where(x => x.AttemptId == attemptId).ToArray();
        if (participants.Length == 0 || versions.Count != participants.Length ||
            participants.Any(p => !versions.TryGetValue(p.Slug, out var version) || string.IsNullOrWhiteSpace(version))) return false;
        return Complete(attemptId, participants.Select(p => new SharedPluginState(p.Slug, versions[p.Slug], p.TargetState)), true);
    }

    /// <summary>
    /// In one write, removes the entries of plugins that are no longer installed (<paramref name="forget"/>; their
    /// accepted licenses and shown welcome pages stay recorded) and sets whether entries are <see
    /// cref="SharedPluginState.Missing"/>. Refuses when one of them is blocked by a local attempt or a teammate changed its
    /// entry on disk to another value.
    /// </summary>
    public bool TryUpdateMissing(IReadOnlyCollection<string> forget, IReadOnlyCollection<string> missing,
        IReadOnlyCollection<string> found)
    {
        var touched = forget.Concat(missing).Concat(found).ToArray();
        if (IsReadOnly || !HasSharedFile || touched.Any(IsBlocked) || touched.Any(s => !_shared.ContainsKey(s)))
        {
            return false;
        }

        var next = new Dictionary<string, SharedPluginState>(_shared, StringComparer.Ordinal);
        foreach (var slug in forget) next.Remove(slug);
        foreach (var slug in missing) next[slug] = next[slug] with { Missing = true };
        foreach (var slug in found) next[slug] = next[slug] with { Missing = false };
        return SaveShared(next, _lastCompletedAttemptId, onlyWhenChanged: true);
    }

    private bool Complete(Guid attemptId, IEnumerable<SharedPluginState> completedStates, bool acknowledgeVersions)
    {
        if (IsReadOnly)
        {
            return false;
        }

        var participants = _local.Values.Where(x => x.AttemptId == attemptId).ToArray();
        var completed = completedStates.ToArray();
        if (participants.Length == 0 || completed.Length != participants.Length ||
            completed.Any(x => !ValidShared(x)) ||
            !participants.All(x => completed.Any(y => y.Slug == x.Slug && y.State == x.TargetState)))
        {
            logger?.Error("Plugin state completion does not match the local attempt.");
            return false;
        }

        var next = new Dictionary<string, SharedPluginState>(_shared, StringComparer.Ordinal);
        foreach (var entry in completed)
        {
            next[entry.Slug] = entry with { Version = !acknowledgeVersions && _shared.TryGetValue(entry.Slug, out var old)
                ? old.Version
                : entry.Version };
        }

        // Recovery asks whether an update was acknowledged, so only an acknowledgement records its attempt. Enabling and
        // disabling keep the committed file as it is when no entry changed, so teammates do not get a needless change.
        if (!SaveShared(next, acknowledgeVersions ? attemptId : _lastCompletedAttemptId,
                touched: participants.Select(p => p.Slug).ToArray(), onlyWhenChanged: !acknowledgeVersions))
        {
            return false;
        }

        var remaining = new Dictionary<string, LocalPluginAttempt>(_local, StringComparer.Ordinal);
        foreach (var participant in participants)
        {
            remaining.Remove(participant.Slug);
        }

        _local = remaining; // for an update, the shared attempt ID also makes a failed cleanup harmless on reload
        if (!SaveLocal(remaining))
        {
            logger?.Warn($"Completed plugin state was saved, but {_localPath} could not be cleaned.");
        }

        return true;
    }

    /// <param name="touched">Plugins whose entries this write decides, also when their value stays the same.</param>
    /// <param name="onlyWhenChanged">Leaves the file as it is when nothing in it would change.</param>
    private bool SaveShared(Dictionary<string, SharedPluginState> states, Guid? completedAttemptId,
        Dictionary<string, AcceptedLicense>? licenses = null, Dictionary<string, ShownWelcome>? welcomes = null,
        IReadOnlyCollection<string>? touched = null, bool onlyWhenChanged = false)
    {
        var entries = new SharedStateMerge.Entries(states, licenses ?? _licenses, welcomes ?? _welcomes, completedAttemptId);
        try
        {
            var actual = File.Exists(_sharedPath) ? File.ReadAllBytes(_sharedPath) : null;
            if (!SharedStateMerge.EqualBytes(actual, _sharedBytes))
            {
                if (Rebase(actual, touched ?? [], entries) is not { } rebased) return false;
                entries = rebased;
            }

            if (onlyWhenChanged && SharedStateMerge.SameEntries(entries.States, _shared) &&
                SharedStateMerge.SameEntries(entries.Licenses, _licenses) &&
                SharedStateMerge.SameEntries(entries.Welcomes, _welcomes) &&
                entries.CompletedAttemptId == _lastCompletedAttemptId &&
                _unsavedLicenses.Count == 0 && _unsavedWelcomes.Count == 0)
            {
                return true;
            }

            var bytes = WriteShared(entries.States.Values, entries.CompletedAttemptId, entries.Licenses.Values,
                entries.Welcomes.Values);
            AtomicFile.Write(_sharedPath, bytes);
            _shared = entries.States;
            _licenses = entries.Licenses;
            _welcomes = entries.Welcomes;
            _sharedBytes = bytes;
            _lastCompletedAttemptId = entries.CompletedAttemptId;
            _unsavedLicenses.Clear();
            _unsavedWelcomes.Clear();
            return true;
        }
        catch (Exception ex)
        {
            logger?.Error($"Cannot save finalized plugin state at {_sharedPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Moves the entries of a write (<paramref name="ours"/>) onto a shared file that changed on disk since it was read,
    /// e.g. by a git pull: the file becomes the store's state, and the entries the write changed (and
    /// <paramref name="touched"/>) are set in it. Returns null, leaving the store as it was, when the file is gone, was
    /// created, or cannot be read, and null with the file as the store's state when it changed one of those entries to
    /// another value. Logs why it refuses.
    /// </summary>
    private SharedStateMerge.Entries? Rebase(byte[]? actual, IReadOnlyCollection<string> touched, SharedStateMerge.Entries ours)
    {
        if (actual is null)
        {
            logger?.Error($"Plugin state at {_sharedPath} was deleted since it was read; refusing to write it again.");
            return null;
        }

        if (_sharedBytes is null)
        {
            logger?.Error($"Plugin state at {_sharedPath} was created on disk since the store was read; refusing to overwrite it.");
            return null;
        }

        SharedDocument disk;
        try
        {
            disk = ReadShared(actual);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or UnsupportedSchemaException)
        {
            logger?.Error($"Plugin state at {_sharedPath} changed on disk and cannot be read: {ex.Message} Refusing to overwrite it.");
            return null;
        }

        var original = new SharedStateMerge.Entries(_shared, _licenses, _welcomes, _lastCompletedAttemptId);
        UseShared(disk);
        // Also when this write is refused, the store keeps what is on disk now: a retry builds its entries from it (e.g.
        // the version a teammate's update recorded) instead of running into the same conflict again.
        _sharedBytes = actual;
        var merged = SharedStateMerge.Merge(original, ours,
            new SharedStateMerge.Entries(_shared, _licenses, _welcomes, _lastCompletedAttemptId), touched);
        if (merged is null)
        {
            logger?.Error($"Plugin state changed on disk at {_sharedPath} in the same entries; refusing to overwrite it.");
            return null;
        }

        logger?.Log($"Plugin state changed on disk at {_sharedPath}; its other entries are kept.");
        return merged;
    }

    private static void CleanTemporaryFiles(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var temporaryPath in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".tmp-*"))
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* removed at a later start */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* removed at a later start */ }
    }

    private static bool ValidShared(SharedPluginState state) =>
        ValidSlug(state.Slug) && !string.IsNullOrWhiteSpace(state.Version) && IsCompleted(state.State);

    private static bool ValidLicense(AcceptedLicense license) =>
        ValidSlug(license.Slug) && !string.IsNullOrWhiteSpace(license.License);
}
#endif
