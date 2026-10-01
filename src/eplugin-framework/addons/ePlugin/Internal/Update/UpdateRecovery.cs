#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin.Internal.Update;

// Deliberately independent of EGlobal, recipe execution, and the regular journal reader.
// Preserve unknown JSON fields: this runs in a different framework version from the writer.
internal static class UpdateRecovery
{
    internal enum Outcome { Continue, Restart, ManualRepair }
    private sealed class Plugin
    {
        public string Slug { get; set; } = "";
        public string OldVersion { get; set; } = "";
        public bool SwapStarted { get; set; }
    }
    private sealed class Snapshot { public int SchemaVersion { get; set; } = 1; public List<string> Directories { get; set; } = []; }
    private sealed class Recipe
    {
        public Snapshot Old { get; set; } = new();
        public Snapshot Applied { get; set; } = new();
        public JsonElement Pending { get; set; }
    }
    private sealed class Journal
    {
        public int Schema { get; set; }
        public Guid AttemptId { get; set; }
        public string State { get; set; } = "";
        public int StartCount { get; set; }
        public List<Plugin> Plugins { get; set; } = [];
        public List<string> ProjectFiles { get; set; } = [];
        public Dictionary<string, Recipe> Recipes { get; set; } = [];
    }
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static Outcome RunIfNeeded(string projectRoot, ILogger logger, Func<bool> rebuild, Action restoreSettings)
    {
        var root = Path.Combine(projectRoot, ".godot/eplugin/updates");
        if (!Directory.Exists(root)) return Outcome.Continue;
        foreach (var folder in Directory.GetDirectories(root))
        {
            var path = Path.Combine(folder, "journal.json");
            if (!File.Exists(path)) continue;
            Guid? recoveringAttempt = null;
            try
            {
                var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("Empty journal.");
                if (json["plugins"] is not JsonArray plugins || !plugins.Any(p => p?["slug"]?.GetValue<string>() == "ePlugin")) continue;
                var journal = json.Deserialize<Journal>(Options) ?? throw new InvalidDataException("Empty journal.");
                if (!journal.Plugins.Any(p => p.Slug == "ePlugin")) continue;
                if (journal.Schema != 1 || journal.AttemptId == Guid.Empty) throw new InvalidDataException("Unsupported recovery journal schema.");
                if (journal.State is "Committed" or "RolledBack" or "Failed" or "Verified" || File.Exists(Path.Combine(folder, "healthy.marker"))) continue;
                // Staged has not touched addon files. Normal recovery can abandon it.
                if (journal.State == "Staged") continue;
                json["startCount"] = ++journal.StartCount;
                Write(path, json);
                if (journal.StartCount < 2) continue;
                logger.Warn("Self-update has not become healthy after two starts. Restoring backups: " + folder);
                var store = new PluginStateStore(Path.Combine(projectRoot, "addons/eplugin-state.json"), logger);
                store.Load();
                if (store.LastCompletedAttemptId == journal.AttemptId) continue;
                if (store.IsReadOnly) throw new IOException("Plugin state is read-only; restore it before recovery.");
                if (journal.Recipes.Values.Any(r => r.Old.SchemaVersion != 1 || r.Applied.SchemaVersion != 1)) throw new InvalidDataException("Unsupported recovery recipe schema.");
                // Validate every path and required backup before changing any files.
                foreach (var plugin in journal.Plugins)
                {
                    Slug(plugin.Slug);
                    var installed = Inside(Path.Combine(projectRoot, "addons"), plugin.Slug);
                    var backup = Inside(Path.Combine(folder, "backup"), plugin.Slug);
                    if (plugin.SwapStarted && !Directory.Exists(backup) &&
                        (!File.Exists(Path.Combine(installed, "plugin.cfg")) || !HasVersion(Path.Combine(installed, "plugin.cfg"), plugin.OldVersion)))
                        throw new IOException("Missing backup for " + plugin.Slug);
                }
                foreach (var file in journal.ProjectFiles)
                {
                    if (file.Contains('/') || file.Contains('\\') || !File.Exists(Inside(Path.Combine(folder, "backup-project"), file)))
                        throw new IOException("Missing or invalid project backup: " + file);
                }
                recoveringAttempt = journal.AttemptId;
                json["state"] = "RollingBack"; Write(path, json);
                foreach (var plugin in journal.Plugins.AsEnumerable().Reverse())
                {
                    var backup = Inside(Path.Combine(folder, "backup"), plugin.Slug);
                    if (!Directory.Exists(backup)) continue;
                    var installed = Inside(Path.Combine(projectRoot, "addons"), plugin.Slug);
                    if (Directory.Exists(installed)) Directory.Delete(installed, true);
                    // Copy, rather than consume, backups so interrupted early recovery is repeatable.
                    Copy(backup, installed);
                }
                foreach (var recipe in journal.Recipes.Where(r => !journal.Plugins.Any(p => p.Slug == r.Key)))
                {
                    Slug(recipe.Key);
                    var addon = Inside(Path.Combine(projectRoot, "addons"), recipe.Key);
                    var applied = recipe.Value.Applied.Directories.ToList();
                    if (recipe.Value.Pending.ValueKind == JsonValueKind.Object && recipe.Value.Pending.TryGetProperty("directory", out var pending) && pending.ValueKind == JsonValueKind.String)
                        applied.Add(pending.GetString()!);
                    foreach (var dir in applied.Where(d => !recipe.Value.Old.Directories.Contains(d)).OrderByDescending(d => d.Length)) Visibility(addon, recipe.Key, dir, false);
                    foreach (var dir in recipe.Value.Old.Directories.OrderBy(d => d.Length)) Visibility(addon, recipe.Key, dir, true);
                }
                foreach (var file in journal.ProjectFiles) File.Copy(Inside(Path.Combine(folder, "backup-project"), file), Inside(projectRoot, file), true);
                if (!journal.ProjectFiles.Any(f => f.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)) && File.Exists(Path.Combine(projectRoot, "nuget.config"))) File.Delete(Path.Combine(projectRoot, "nuget.config"));
                restoreSettings();
                if (!rebuild()) throw new IOException("Restored project failed to build.");
                if (store.LocalAttempts.Any(a => a.AttemptId == journal.AttemptId) && !store.TryAbandonAttempt(journal.AttemptId)) throw new IOException("Cannot clear restored update marker.");
                json["state"] = "RolledBack";
                json["failure"] = "Early self-update recovery restored the previous framework after two unhealthy starts.";
                Write(path, json);
                try
                {
                    var cachePath = Path.Combine(projectRoot, ".godot/eplugin/update-state.json");
                    var cache = File.Exists(cachePath) ? JsonNode.Parse(File.ReadAllText(cachePath)) as JsonObject : null;
                    cache ??= new JsonObject { ["schema"] = 1 };
                    var failures = cache["failedUpdates"] as JsonObject;
                    if (failures is null) cache["failedUpdates"] = failures = new JsonObject();
                    foreach (var plugin in plugins)
                    {
                        var slug = plugin!["slug"]!.GetValue<string>();
                        var entries = failures[slug] as JsonArray;
                        if (entries is null) failures[slug] = entries = new JsonArray();
                        entries.Add(new JsonObject { ["version"] = plugin["newVersion"]!.GetValue<string>(), ["reason"] = json["failure"]!.GetValue<string>(), ["utc"] = DateTimeOffset.UtcNow.ToString("O") });
                    }
                    Write(cachePath, cache);
                }
                catch (Exception ex) { logger.Warn("Cannot record self-update failure history: " + ex.Message); }
                logger.Warn("Self-update restored. Restart the editor. Recovery log: " + folder);
                return Outcome.Restart;
            }
            catch (Exception ex)
            {
                if (recoveringAttempt is { } attempt)
                {
                    try
                    {
                        var store = new PluginStateStore(Path.Combine(projectRoot, "addons/eplugin-state.json"), logger);
                        store.Load(); store.TryFail(attempt, PersistedPluginState.Failed, "update_rollback_failed");
                    }
                    catch (Exception) { /* Preserve backups even if the marker cannot be written. */ }
                }
                logger.Error($"Self-update recovery needs manual repair: {ex.Message}. Close the editor, restore {folder}/backup/<slug> to addons/<slug> and backup-project files to the project root, delete .godot/mono/temp, rebuild, then use Retry failed ePlugin addons. See {folder}/README.txt.");
                return Outcome.ManualRepair;
            }
        }
        return Outcome.Continue;
    }
    private static void Write(string path, JsonObject json)
    {
        var temporary = path + ".recovery.tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, json, Options); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    private static void Slug(string slug) { if (slug.Contains('/') || slug.Contains('\\')) throw new InvalidDataException("Invalid addon slug."); Inside("/", slug); }
    private static string Inside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is "" or "." or "..")) throw new InvalidDataException("Unsafe recovery path.");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Recovery path escapes its root.");
        return path;
    }
    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.GetFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
            var to = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Copy(entry, to); else File.Copy(entry, to, true);
        }
    }
    private static bool HasVersion(string path, string version) => File.ReadLines(path).Any(line => line.Trim() == "version=\"" + version + "\"" || line.Trim() == "version = \"" + version + "\"");
    private static void Visibility(string addon, string slug, string path, bool show)
    {
        var prefix = "res://addons/" + slug + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Invalid recipe directory in recovery.");
        var hidden = path[prefix.Length..];
        var slash = hidden.LastIndexOf('/');
        var visible = slash < 0 ? hidden.TrimStart('.') : hidden[..(slash + 1)] + hidden[(slash + 1)..].TrimStart('.');
        if (!(slash < 0 ? hidden : hidden[(slash + 1)..]).StartsWith('.')) hidden = slash < 0 ? "." + hidden : hidden[..(slash + 1)] + "." + hidden[(slash + 1)..];
        var from = Inside(addon, show ? hidden : visible); var to = Inside(addon, show ? visible : hidden);
        if (from != to && Directory.Exists(from) && !Directory.Exists(to))
        {
            File.SetAttributes(from, show ? FileAttributes.Normal : FileAttributes.Hidden);
            Directory.Move(from, to);
        }
    }
}
#endif
