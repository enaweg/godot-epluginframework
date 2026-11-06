#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Moves a write of the shared plugin state file onto a file that changed on disk since it was read, e.g. by a git pull.
/// Pure: <see cref="PluginStateStore"/> reads the file and decides what to log.
/// </summary>
internal static class SharedStateMerge
{
    /// <summary>The entries of a shared file, as read or as a write sets them.</summary>
    internal sealed record Entries(
        Dictionary<string, SharedPluginState> States,
        Dictionary<string, AcceptedLicense> Licenses,
        Dictionary<string, ShownWelcome> Welcomes,
        Guid? CompletedAttemptId);

    /// <summary>
    /// <paramref name="disk"/> with the entries <paramref name="ours"/> changed against <paramref name="original"/> (and the
    /// plugin entries of <paramref name="touched"/>); null when the disk changed one of them as well, to another value.
    /// </summary>
    public static Entries? Merge(Entries original, Entries ours, Entries disk, IReadOnlyCollection<string> touched)
    {
        var states = Merge(original.States, ours.States, disk.States, touched);
        var licenses = Merge(original.Licenses, ours.Licenses, disk.Licenses, []);
        var welcomes = Merge(original.Welcomes, ours.Welcomes, disk.Welcomes, []);
        var completedChanged = ours.CompletedAttemptId != original.CompletedAttemptId;
        if (states is null || licenses is null || welcomes is null ||
            completedChanged && disk.CompletedAttemptId != original.CompletedAttemptId &&
            disk.CompletedAttemptId != ours.CompletedAttemptId)
        {
            return null;
        }

        return new(states, licenses, welcomes, completedChanged ? ours.CompletedAttemptId : disk.CompletedAttemptId);
    }

    /// <summary>
    /// <paramref name="disk"/> with the entries <paramref name="ours"/> changed against <paramref name="original"/>, and
    /// those of <paramref name="touched"/>; null when the disk changed one of them as well, to another value than ours
    /// (a teammate who made the same change does not conflict with it).
    /// </summary>
    public static Dictionary<string, T>? Merge<T>(Dictionary<string, T> original, Dictionary<string, T> ours,
        Dictionary<string, T> disk, IReadOnlyCollection<string> touched)
    {
        var changed = original.Keys.Union(ours.Keys).Where(slug => !Equals(original.GetValueOrDefault(slug), ours.GetValueOrDefault(slug)))
            .Union(touched).ToArray();
        if (changed.Any(slug => !Equals(original.GetValueOrDefault(slug), disk.GetValueOrDefault(slug)) &&
                                !Equals(ours.GetValueOrDefault(slug), disk.GetValueOrDefault(slug))))
        {
            return null;
        }

        var merged = new Dictionary<string, T>(disk, StringComparer.Ordinal);
        foreach (var slug in changed)
        {
            if (ours.TryGetValue(slug, out var value)) merged[slug] = value;
            else merged.Remove(slug);
        }

        return merged;
    }

    public static bool SameEntries<T>(Dictionary<string, T> first, Dictionary<string, T> second) =>
        first.Count == second.Count && first.All(x => second.TryGetValue(x.Key, out var y) && Equals(x.Value, y));

    public static bool EqualBytes(byte[]? first, byte[]? second) =>
        first is null ? second is null : second is not null && first.SequenceEqual(second);
}
#endif
