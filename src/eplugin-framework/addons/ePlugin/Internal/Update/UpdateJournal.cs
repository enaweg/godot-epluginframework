#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enaweg.Plugin.Internal.Update;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum UpdatePhase { Staged, Swapped, InterimBuilt, Reconciling, Reconciled, AwaitingDecision, Verified, RollingBack, RolledBack, Committed, CommittedWithErrors, Failed }
internal sealed class UpdatePluginJournal
{
    public string Slug { get; set; } = "";
    public bool IsEPlugin { get; set; }
    public bool WasEnabled { get; set; }
    public string OldVersion { get; set; } = "";
    public string NewVersion { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string? Revision { get; set; }
    public bool ContainsCSharp { get; set; }
    public bool SwapStarted { get; set; }
    public List<string> Preserved { get; set; } = [];
    public Dictionary<string, string> Uids { get; set; } = [];
}
internal sealed record JournalBuild(string Name, int ExitCode, string LogFile);
internal sealed class UpdateJournal
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = "";
    public Guid AttemptId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public UpdatePhase State { get; set; }
    public bool RestartRequired { get; set; }
    public int StartCount { get; set; }
    public List<UpdatePluginJournal> Plugins { get; set; } = [];
    public List<JournalBuild> Builds { get; set; } = [];
    public List<string> ProjectFiles { get; set; } = [];
    public Dictionary<string, RecipeJournal> Recipes { get; set; } = [];
    public Dictionary<string, string> AdditionalVersions { get; set; } = [];
    public string? Failure { get; set; }
    [JsonIgnore] public string Directory { get; set; } = "";
    public string Backup(string slug) => PackageFiles.Inside(Path.Combine(Directory, "backup"), slug);
    public string Staging(string slug) => PackageFiles.Inside(Path.Combine(Directory, "staging"), slug);
    public bool IsActive => State is not (UpdatePhase.RolledBack or UpdatePhase.Committed or UpdatePhase.CommittedWithErrors or UpdatePhase.Failed);
    public void Save(UpdatePhase? state = null) { if (state is not null) State = state.Value; AtomicJson.Write(Path.Combine(Directory, "journal.json"), this); }
    public static UpdateJournal Load(string directory)
    {
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(directory, "journal.json")), UpdateStateStore.JsonOptions) ?? throw new InvalidDataException("Empty update journal.");
        if (journal.Schema is < 1 or > 1 || journal.Id != Path.GetFileName(directory) || journal.AttemptId == Guid.Empty || journal.Plugins.Count == 0 ||
            journal.Plugins.Select(p => p.Slug).Distinct(StringComparer.Ordinal).Count() != journal.Plugins.Count)
            throw new InvalidDataException("Unsupported or invalid update journal; restore its backups manually.");
        foreach (var plugin in journal.Plugins)
            if (PackageFiles.Normalize(plugin.Slug) != plugin.Slug || plugin.Slug.Contains('/') ||
                !SemVer.TryParse(plugin.OldVersion, out _) || !SemVer.TryParse(plugin.NewVersion, out _))
                throw new InvalidDataException("Invalid plugin in update journal.");
        foreach (var path in journal.ProjectFiles) PackageFiles.Normalize(path);
        foreach (var recipe in journal.Recipes)
        {
            if (PackageFiles.Normalize(recipe.Key) != recipe.Key || recipe.Key.Contains('/')) throw new InvalidDataException("Invalid recipe owner.");
            RecipeReconciler.Validate(recipe.Value.Old);
            RecipeReconciler.Validate(recipe.Value.Applied);
            if (recipe.Value.Target is not null) RecipeReconciler.Validate(recipe.Value.Target);
        }
        foreach (var plugin in journal.Plugins) foreach (var path in plugin.Preserved) PackageFiles.Normalize(path);
        journal.Directory = directory;
        return journal;
    }
}
internal sealed class UpdateJournals(string root, Action<string>? log = null)
{
    public IReadOnlyList<UpdateJournal> Read()
    {
        var result = new List<UpdateJournal>();
        if (!System.IO.Directory.Exists(root)) return result;
        foreach (var folder in System.IO.Directory.GetDirectories(root))
        {
            if (!File.Exists(Path.Combine(folder, "journal.json"))) continue;
            try { result.Add(UpdateJournal.Load(folder)); }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException) { log?.Invoke($"Cannot read update journal {folder}: {ex.Message}"); }
        }
        return result;
    }
    public bool OwnsAttempt(Guid? id) => id is not null && Read().Any(j => j.IsActive && j.AttemptId == id);
    public IReadOnlySet<string> OwnedSlugs => Read().Where(j => j.IsActive).SelectMany(j => j.Plugins.Select(p => p.Slug).Concat(j.AdditionalVersions.Keys)).ToHashSet(StringComparer.Ordinal);
    public string NewDirectory() => Path.Combine(root, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
}
#endif
