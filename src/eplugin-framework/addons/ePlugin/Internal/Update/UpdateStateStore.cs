#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Enaweg.Plugin.Internal.Update;

internal sealed record AppliedRevision(string Source, string? Revision, DateTimeOffset AppliedUtc);
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
internal static class AtomicJson
{
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, UpdateStateStore.JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
internal static class UpdateScheduler
{
    public static bool ShouldCheck(DateTimeOffset now, DateTimeOffset? last, bool enabled, double intervalHours = 20) =>
        enabled && (last is null || now - last.Value >= TimeSpan.FromHours(Math.Clamp(intervalHours, 1, 8760)));

    public static IReadOnlyList<UpdateCandidate> CurrentCached(UpdateCache cache, IReadOnlyList<PluginUpdateTarget> targets,
        bool allowPrerelease) => cache.Results.Where(candidate => targets.Any(t => t.Slug == candidate.Slug &&
            candidate.SourceUrl is { } source && t.UpdateUrls.Contains(source) && SemVer.TryParse(t.InstalledVersion, out var installed) &&
            SemVer.TryParse(candidate.NewVersion, out var remote) && remote.CompareTo(installed) > 0 &&
            (allowPrerelease || remote.Prerelease is null)))
        .Select(c => c with { InstalledVersion = targets.First(t => t.Slug == c.Slug).InstalledVersion }).ToArray();
}
#endif
