#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin.Internal;

internal enum PersistedPluginState
{
    Activated,
    Deactivated,
    Failed,
    Invalid
}

internal sealed record SharedPluginState(string Slug, string Version, PersistedPluginState State);

internal sealed record LocalPluginAttempt(
    Guid AttemptId,
    string Slug,
    string? InstalledVersion,
    PersistedPluginState TargetState,
    PersistedPluginState State,
    string Reason);

/// <summary>
/// The committed file contains only completed states. The adjacent .user file is a local journal
/// that blocks an automatic retry when a transition fails or an assembly reload interrupts it.
/// </summary>
internal sealed class PluginStateStore(string sharedPath, ILogger? logger)
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly string _sharedPath = sharedPath;
    private readonly string _localPath = sharedPath + ".user";
    private Dictionary<string, SharedPluginState> _shared = new(StringComparer.Ordinal);
    private Dictionary<string, LocalPluginAttempt> _local = new(StringComparer.Ordinal);
    private byte[]? _sharedBytes;
    private Guid? _lastCompletedAttemptId;

    public Guid? LastCompletedAttemptId => _lastCompletedAttemptId;
    public Guid? GetAttemptId(string slug) => GetLocal(slug)?.AttemptId;
    public bool IsReadOnly { get; private set; }
    public bool HasSharedFile => _sharedBytes is not null;
    public IReadOnlyCollection<SharedPluginState> SharedStates => _shared.Values;
    public IReadOnlyCollection<LocalPluginAttempt> LocalAttempts => _local.Values;

    public SharedPluginState? GetShared(string slug) => _shared.GetValueOrDefault(slug);
    public LocalPluginAttempt? GetLocal(string slug) => _local.GetValueOrDefault(slug);
    public bool IsBlocked(string slug) => _local.ContainsKey(slug);

    public bool Load()
    {
        try
        {
            CleanTemporaryFiles(_sharedPath);
            CleanTemporaryFiles(_localPath);
            _sharedBytes = File.Exists(_sharedPath) ? File.ReadAllBytes(_sharedPath) : null;
            var localBytes = File.Exists(_localPath) ? File.ReadAllBytes(_localPath) : null;
            var shared = _sharedBytes is null
                ? new SharedDocument { SchemaVersion = SchemaVersion }
                : JsonSerializer.Deserialize<SharedDocument>(_sharedBytes, JsonOptions)
                  ?? throw new InvalidDataException("Shared plugin state is null.");
            var local = localBytes is null
                ? new LocalDocument { SchemaVersion = SchemaVersion }
                : JsonSerializer.Deserialize<LocalDocument>(localBytes, JsonOptions)
                  ?? throw new InvalidDataException("Local plugin state is null.");
            Validate(shared, local);
            _shared = shared.Plugins!.ToDictionary(x => x.Slug!, x =>
                new SharedPluginState(x.Slug!, x.Version!, x.State!.Value), StringComparer.Ordinal);
            _local = local.Attempts!.ToDictionary(x => x.Slug!, x =>
                new LocalPluginAttempt(x.AttemptId!.Value, x.Slug!, x.InstalledVersion,
                    x.TargetState!.Value, x.State!.Value, x.Reason!), StringComparer.Ordinal);
            _lastCompletedAttemptId = shared.LastCompletedAttemptId;
            IsReadOnly = false;

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
            logger?.Error($"Cannot load plugin state at {_sharedPath} or {_localPath}: {ex.Message}. Files were preserved.");
            return false;
        }
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
            if (IsBlocked(state.Slug) || !ValidShared(state) || !next.TryAdd(state.Slug, state))
            {
                if (IsBlocked(state.Slug))
                {
                    continue;
                }

                logger?.Error($"Cannot create plugin state baseline for {state.Slug}: invalid or duplicate entry.");
                return false;
            }
        }

        return SaveShared(next, null);
    }

    public bool TryBeginAttempt(string slug, string? installedVersion, PersistedPluginState targetState,
        out Guid attemptId, bool manualRetry = false)
    {
        attemptId = Guid.Empty;
        if (IsReadOnly || !ValidSlug(slug) || !IsCompleted(targetState))
        {
            return false;
        }

        var next = new Dictionary<string, LocalPluginAttempt>(_local, StringComparer.Ordinal);
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

        attemptId = Guid.NewGuid();
        next[slug] = new LocalPluginAttempt(attemptId, slug, installedVersion, targetState,
            PersistedPluginState.Failed, "attempt_in_progress");
        if (!SaveLocal(next))
        {
            attemptId = Guid.Empty;
            return false;
        }

        _local = next;
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
            return existing.AttemptId == attemptId && existing.TargetState == targetState;
        }

        var next = new Dictionary<string, LocalPluginAttempt>(_local, StringComparer.Ordinal)
        {
            [slug] = new LocalPluginAttempt(attemptId, slug, installedVersion, targetState,
                PersistedPluginState.Failed, "attempt_in_progress")
        };
        if (!SaveLocal(next))
        {
            return false;
        }

        _local = next;
        return true;
    }

    public bool TryFail(Guid attemptId, PersistedPluginState failureState, string reason)
    {
        if (IsReadOnly || failureState is not (PersistedPluginState.Failed or PersistedPluginState.Invalid) ||
            string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        var next = new Dictionary<string, LocalPluginAttempt>(_local, StringComparer.Ordinal);
        var found = false;
        foreach (var attempt in _local.Values.Where(x => x.AttemptId == attemptId))
        {
            next[attempt.Slug] = attempt with { State = failureState, Reason = reason };
            found = true;
        }

        if (!found || !SaveLocal(next))
        {
            return false;
        }

        _local = next;
        return true;
    }

    public bool TryRecordInvalid(string slug, string? installedVersion, string reason)
    {
        if (!TryBeginAttempt(slug, installedVersion, PersistedPluginState.Activated, out var attemptId))
        {
            return false;
        }

        return TryFail(attemptId, PersistedPluginState.Invalid, reason);
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

    public bool TryAbandonAttempt(Guid attemptId)
    {
        if (IsReadOnly || !_local.Values.Any(p => p.AttemptId == attemptId)) return false;
        var remaining = _local.Where(p => p.Value.AttemptId != attemptId).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (!SaveLocal(remaining)) return false;
        _local = remaining;
        return true;
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

        if (!SaveShared(next, attemptId))
        {
            return false;
        }

        var remaining = new Dictionary<string, LocalPluginAttempt>(_local, StringComparer.Ordinal);
        foreach (var participant in participants)
        {
            remaining.Remove(participant.Slug);
        }

        _local = remaining; // the shared attempt ID also makes a failed cleanup harmless on reload
        if (!SaveLocal(remaining))
        {
            logger?.Warn($"Completed plugin state was saved, but {_localPath} could not be cleaned.");
        }

        return true;
    }

    private bool SaveShared(Dictionary<string, SharedPluginState> states, Guid? completedAttemptId)
    {
        try
        {
            var actual = File.Exists(_sharedPath) ? File.ReadAllBytes(_sharedPath) : null;
            if (!EqualBytes(actual, _sharedBytes))
            {
                logger?.Error($"Plugin state changed on disk at {_sharedPath}; refusing to overwrite it.");
                return false;
            }

            var document = new SharedDocument
            {
                SchemaVersion = SchemaVersion,
                LastCompletedAttemptId = completedAttemptId,
                Plugins = states.Values.OrderBy(x => x.Slug, StringComparer.Ordinal)
                    .Select(x => new SharedEntry
                    {
                        Slug = x.Slug, Version = x.Version, State = x.State
                    }).ToList()
            };
            var bytes = Serialize(document);
            AtomicWrite(_sharedPath, bytes);
            _shared = states;
            _sharedBytes = bytes;
            _lastCompletedAttemptId = completedAttemptId;
            return true;
        }
        catch (Exception ex)
        {
            logger?.Error($"Cannot save finalized plugin state at {_sharedPath}: {ex.Message}");
            return false;
        }
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

            var document = new LocalDocument
            {
                SchemaVersion = SchemaVersion,
                Attempts = attempts.Values.OrderBy(x => x.Slug, StringComparer.Ordinal)
                    .Select(x => new LocalEntry
                    {
                        AttemptId = x.AttemptId,
                        Slug = x.Slug,
                        InstalledVersion = x.InstalledVersion,
                        TargetState = x.TargetState,
                        State = x.State,
                        Reason = x.Reason
                    }).ToList()
            };
            AtomicWrite(_localPath, Serialize(document));
            return true;
        }
        catch (Exception ex)
        {
            logger?.Error($"Cannot save local plugin state at {_localPath}: {ex.Message}");
            return false;
        }
    }

    private static byte[] Serialize<T>(T document) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, JsonOptions) + "\n");

    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void CleanTemporaryFiles(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var temporaryPath in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".tmp-*"))
        {
            File.Delete(temporaryPath);
        }
    }

    private static void Validate(SharedDocument shared, LocalDocument local)
    {
        if (shared.SchemaVersion != SchemaVersion || local.SchemaVersion != SchemaVersion ||
            shared.Plugins is null || local.Attempts is null)
        {
            throw new InvalidDataException("Unsupported or incomplete plugin state schema.");
        }

        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in shared.Plugins)
        {
            if (!ValidSlug(entry.Slug) || string.IsNullOrWhiteSpace(entry.Version) ||
                entry.State is null || !IsCompleted(entry.State.Value) || !slugs.Add(entry.Slug!))
            {
                throw new InvalidDataException("Shared plugin state has an invalid or duplicate entry.");
            }
        }

        slugs.Clear();
        foreach (var entry in local.Attempts)
        {
            if (entry.AttemptId is null || entry.AttemptId == Guid.Empty ||
                !ValidSlug(entry.Slug) || entry.TargetState is null || !IsCompleted(entry.TargetState.Value) ||
                entry.State is not (PersistedPluginState.Failed or PersistedPluginState.Invalid) ||
                string.IsNullOrWhiteSpace(entry.Reason) || !slugs.Add(entry.Slug!))
            {
                throw new InvalidDataException("Local plugin state has an invalid or duplicate attempt.");
            }
        }
    }

    private static bool IsCompleted(PersistedPluginState state) =>
        state is PersistedPluginState.Activated or PersistedPluginState.Deactivated;

    private static bool ValidShared(SharedPluginState state) =>
        ValidSlug(state.Slug) && !string.IsNullOrWhiteSpace(state.Version) && IsCompleted(state.State);

    private static bool ValidSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && slug != "." && slug != ".." &&
        slug.IndexOfAny(['/', '\\']) < 0;

    private static bool EqualBytes(byte[]? first, byte[]? second) =>
        first is null ? second is null : second is not null && first.SequenceEqual(second);

    private sealed class SharedDocument
    {
        public int SchemaVersion { get; set; }
        public Guid? LastCompletedAttemptId { get; set; }
        public List<SharedEntry>? Plugins { get; set; } = [];
    }

    private sealed class SharedEntry
    {
        public string? Slug { get; set; }
        public string? Version { get; set; }
        public PersistedPluginState? State { get; set; }
    }

    private sealed class LocalDocument
    {
        public int SchemaVersion { get; set; }
        public List<LocalEntry>? Attempts { get; set; } = [];
    }

    private sealed class LocalEntry
    {
        public Guid? AttemptId { get; set; }
        public string? Slug { get; set; }
        public string? InstalledVersion { get; set; }
        public PersistedPluginState? TargetState { get; set; }
        public PersistedPluginState? State { get; set; }
        public string? Reason { get; set; }
    }
}
#endif
