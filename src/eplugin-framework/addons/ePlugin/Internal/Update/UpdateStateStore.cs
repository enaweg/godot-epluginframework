#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Enaweg.Plugin.Internal.Update;

/// <param name="Version">The plugin.cfg version the revision installed; null in caches written before it was recorded.</param>
internal sealed record AppliedRevision(string Source, string? Revision, DateTimeOffset AppliedUtc, string? Version = null);
internal sealed record FailedUpdate(string Version, string Reason, DateTimeOffset Utc);
internal sealed class UpdateCache
{
    public int Schema { get; set; } = 1;
    public DateTimeOffset? LastCheckUtc { get; set; }
    public List<UpdateCandidate> Results { get; set; } = [];
    public Dictionary<string, AppliedRevision> Revisions { get; set; } = [];
    public Dictionary<string, List<FailedUpdate>> FailedUpdates { get; set; } = [];
}
internal interface IUpdateStateStore
{
    UpdateCache State { get; }
    void Save();
}
internal sealed class UpdateStateStore(string path) : IUpdateStateStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public UpdateCache State { get; private set; } = new();
    public void Load()
    {
        try
        {
            var state = JsonSerializer.Deserialize<UpdateCache>(File.ReadAllText(path), JsonOptions);
            State = state is { Schema: 1, Results: not null, Revisions: not null, FailedUpdates: not null } ? state : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { State = new(); }
    }
    public void Save()
    {
        try { AtomicJson.Write(path, State); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* disposable local cache */ }
    }
}
/// <summary>Replaces a file in one step, so nobody reads it half-written and a failure leaves the previous one.</summary>
internal static class AtomicFile
{
    /// <param name="flushToDisk">
    /// Whether the content reaches the disk before it replaces the file; otherwise a crash right after may leave it empty.
    /// Only files that are rewritten anyway, such as the progress window's, skip it.
    /// </param>
    public static void Write(string path, Action<Stream> write, bool flushToDisk = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Write(string path, byte[] bytes) => Write(path, stream => stream.Write(bytes));
}
internal static class AtomicJson
{
    public static void Write<T>(string path, T value) =>
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, value, UpdateStateStore.JsonOptions));
}
internal static class UpdateScheduler
{
    public static bool ShouldCheck(DateTimeOffset now, DateTimeOffset? last, bool enabled, double intervalHours = 20) =>
        enabled && (last is null || now - last.Value >= TimeSpan.FromHours(Math.Clamp(intervalHours, 1, 8760)));

    public static IReadOnlyList<UpdateCandidate> CurrentCached(UpdateCache cache, IReadOnlyList<PluginUpdateTarget> targets) =>
        cache.Results.Where(candidate => targets.Any(t => t.Slug == candidate.Slug &&
            candidate.SourceUrl is { } source && t.UpdateUrls.Contains(source) && SemVer.TryParse(t.InstalledVersion, out var installed) &&
            SemVer.TryParse(candidate.NewVersion, out var remote) && UpdateService.IsUpdate(installed, remote)))
        .Select(c => c with { InstalledVersion = targets.First(t => t.Slug == c.Slug).InstalledVersion }).ToArray();
}
#endif
