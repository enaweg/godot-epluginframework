#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Manager;
using Godot;
using FileAccess = Godot.FileAccess;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    /// <summary>
    /// Every addon with a plugin.cfg directly below res://addons, enabled or not, plus plugins that only remain as a
    /// failed local attempt.
    /// </summary>
    internal IReadOnlyList<PluginInfo> CollectPlugins()
    {
        RefreshPlainPlugins();
        ReloadUpdateSites();
        var enabled = GetEnabledPluginSlugs();
        var types = typeof(EGlobal).Assembly.GetTypes();
        var framework = _ePluginContext?.GetPluginSlug() ?? "ePlugin";
        var plugins = new List<PluginInfo>();
        using var addons = DirAccess.Open("res://addons");
        foreach (var slug in addons?.GetDirectories() ?? [])
        {
            var directory = $"res://addons/{slug}";
            if (!FileAccess.FileExists(directory + "/plugin.cfg")) continue;
            using var config = new ConfigFile();
            if (config.Load(directory + "/plugin.cfg") != Error.Ok) continue;
            string Value(string key) => config.GetValue("plugin", key, "").AsString().Trim();
            var script = PluginCatalog.ResolveScript(directory, Value("script"));
            var context = _contexts.FirstOrDefault(c => c.Slug == slug && c.Plugin is not null);
            var name = Value("name");
            plugins.Add(new PluginInfo(slug, name.Length > 0 ? name : slug,
                PluginCatalog.Classify(script, slug == framework, types), enabled.Contains(slug))
            {
                Version = Value("version"), Author = Value("author"), Description = Value("description"),
                UpdateUrl = Value("update_url") is { Length: > 0 } url ? url : null, UpdateSite = UpdateSiteOf(slug), Script = script,
                DocumentationUrl = Value("documentation_url") is { Length: > 0 } documentation ? documentation : null,
                SourceUrl = Value("source_url") is { Length: > 0 } source ? source : null,
                State = context?.State, Error = context?.ErrorDetail?.Message,
                FailedAttempt = _stateStore?.GetLocal(slug),
                Recipe = context is { IsRecipeCreated: true } ? context.Builder.PluginRecipe : null,
                LocalPackages = LocalIndex.Matching(slug).Count()
            });
        }
        foreach (var attempt in _stateStore?.LocalAttempts ?? [])
        {
            if (plugins.Any(p => p.Slug == attempt.Slug)) continue;
            plugins.Add(new PluginInfo(attempt.Slug, attempt.Slug, PluginKind.Unknown, false)
            { Version = attempt.InstalledVersion ?? "", FailedAttempt = attempt, Missing = true });
        }
        return plugins;
    }
}
#endif
