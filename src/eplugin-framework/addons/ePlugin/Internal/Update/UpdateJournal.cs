#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>
    /// The <see cref="Kinds.PluginKind"/> whose workflow updates the plugin, by name; null in journals written before it
    /// was recorded, which <see cref="Kinds.PluginKinds.Workflow(UpdatePluginJournal)"/> derives from <see cref="IsEPlugin"/>.
    /// </summary>
    public string? Kind { get; set; }
    /// <summary>
    /// The installed or the new version ships a GDExtension, so its files are replaced one by one (see
    /// <see cref="Kinds.EPluginWorkflow"/>); a GDExtension plugin always is.
    /// </summary>
    public bool NativeExtension { get; set; }
    /// <summary>The editor has to restart to load the plugin's native libraries as they are now (GDExtension plugins).</summary>
    public bool RestartRequired { get; set; }
    /// <summary>The new native libraries were reloaded in place, so the editor runs them although it did not restart.</summary>
    public bool ReloadedInPlace { get; set; }
    /// <summary>The editor process <see cref="RestartRequired"/> and <see cref="ReloadedInPlace"/> refer to.</summary>
    public string? NativeSession { get; set; }
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
    /// <summary>
    /// Position in a batch split into independent groups, each its own transaction; null for a batch applied as one.
    /// A group's build failure always rolls back that group, so the remaining groups can still be installed.
    /// </summary>
    public int? Group { get; set; }
    /// <summary>
    /// Started for one plugin from its details, so a build failure may ask whether to keep the new version (see
    /// <see cref="UpdateFailureDecision.Policy"/>); installing all updates rolls back instead.
    /// </summary>
    public bool AskOnBuildFailure { get; set; }
    /// <summary>
    /// Kept, or awaiting the decision, after a build that ran before the recipes were reconciled: Retry failed resumes
    /// the update with reconciling once the project builds.
    /// </summary>
    public bool BeforeReconcile { get; set; }
    /// <summary>Where the errors of the last failed build were; null while no build failed.</summary>
    public BuildFailureCause? FailureCause { get; set; }
    /// <summary>
    /// Whether the plugins compiled on their own after a build failed only in project code; null when that was not
    /// tried. True keeps the update: the project's code has to be adapted to the new version.
    /// </summary>
    public bool? PluginsBuildAlone { get; set; }
    /// <summary>The errors in project code of the last failed build, at most <see cref="BuildDiagnostics.MaxProjectErrors"/>.</summary>
    public List<BuildError> ProjectErrors { get; set; } = [];
    /// <summary>Kept because only project code failed and the plugins compile on their own.</summary>
    [JsonIgnore] public bool KeptForProjectCode => FailureCause == BuildFailureCause.ProjectCode && PluginsBuildAlone == true;
    [JsonIgnore] public string Directory { get; set; } = "";
    public string Backup(string slug) => PackageFiles.Inside(Path.Combine(Directory, "backup"), slug);
    public string Staging(string slug) => PackageFiles.Inside(Path.Combine(Directory, "staging"), slug);
    /// <summary>Where files go that were still in use and could only be moved, not deleted; removed with the journal.</summary>
    public string Discarded(string slug) => PackageFiles.Inside(Path.Combine(Directory, "discarded"), slug);
    public bool IsActive => State is not (UpdatePhase.RolledBack or UpdatePhase.Committed or UpdatePhase.CommittedWithErrors or UpdatePhase.Failed);
    public void Save(UpdatePhase? state = null) { if (state is not null) State = state.Value; AtomicJson.Write(Path.Combine(Directory, "journal.json"), this); }
    public static UpdateJournal Load(string directory)
    {
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(Path.Combine(directory, "journal.json")), UpdateStateStore.JsonOptions) ?? throw new InvalidDataException("Empty update journal.");
        if (journal.Schema is < 1 or > 1 || journal.Id != Path.GetFileName(directory) || journal.AttemptId == Guid.Empty || !Enum.IsDefined(journal.State) || journal.Plugins is null || journal.Plugins.Count == 0 || journal.Recipes is null || journal.ProjectFiles is null || journal.AdditionalVersions is null || journal.Builds is null ||
            journal.Plugins.Select(p => p.Slug).Distinct(StringComparer.Ordinal).Count() != journal.Plugins.Count)
            throw new InvalidDataException("Unsupported or invalid update journal; restore its backups manually.");
        // The old version is empty for a GDExtension add-on installed over a version ePlugin did not know.
        foreach (var plugin in journal.Plugins)
            if (PackageFiles.Normalize(plugin.Slug) != plugin.Slug || plugin.Slug.Contains('/') ||
                !(plugin.OldVersion.Length == 0 || SemVer.TryParse(plugin.OldVersion, out _)) || !SemVer.TryParse(plugin.NewVersion, out _))
                throw new InvalidDataException("Invalid plugin in update journal.");
        foreach (var path in journal.ProjectFiles)
            if (PackageFiles.Normalize(path) != path || path.Contains('/')) throw new InvalidDataException("Invalid project backup path.");
        foreach (var recipe in journal.Recipes)
        {
            if (PackageFiles.Normalize(recipe.Key) != recipe.Key || recipe.Key.Contains('/')) throw new InvalidDataException("Invalid recipe owner.");
            RecipeReconciler.Validate(recipe.Value.Old);
            RecipeReconciler.Validate(recipe.Value.Applied);
            if (recipe.Value.Target is not null) RecipeReconciler.Validate(recipe.Value.Target);
        }
        foreach (var plugin in journal.Plugins) foreach (var path in plugin.Preserved) PackageFiles.Normalize(path);
        // Only shown to the user: a journal without them, or with an invalid list, is still recoverable.
        journal.ProjectErrors = journal.ProjectErrors?.Where(e => e is not null).ToList() ?? [];
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
            // A journal Load refuses is skipped like an unreadable one, so it cannot stop every other update.
            try { result.Add(UpdateJournal.Load(folder)); }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidDataException) { log?.Invoke($"Cannot read update journal {folder}: {ex.Message}"); }
        }
        // Groups of one batch resume in the order they were applied: a later group built on top of the earlier ones.
        return result.OrderBy(j => j.CreatedUtc).ThenBy(j => j.Group ?? 0).ToList();
    }
    public bool OwnsAttempt(Guid? id) => id is not null && Read().Any(j => j.IsActive && j.AttemptId == id);
    public IReadOnlySet<string> OwnedSlugs => Read().Where(j => j.IsActive).SelectMany(j => j.Plugins.Select(p => p.Slug).Concat(j.AdditionalVersions.Keys)).ToHashSet(StringComparer.Ordinal);
    public string NewDirectory() => Path.Combine(root, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
}
#endif
