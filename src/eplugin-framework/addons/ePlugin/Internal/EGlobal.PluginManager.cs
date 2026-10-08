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
            plugins.Add(new PluginInfo(slug, Value("name") is { Length: > 0 } name ? name : slug,
                PluginCatalog.Classify(script, slug == framework, types), enabled.Contains(slug))
            {
                Version = Value("version"), Author = Value("author"), Description = Value("description"),
                UpdateUrl = Value("update_url") is { Length: > 0 } url ? url : null, Script = script,
                State = context?.State, Error = context?.ErrorDetail?.Message,
                FailedAttempt = _stateStore?.GetLocal(slug),
                Recipe = context is { IsRecipeCreated: true } ? context.Builder.PluginRecipe : null
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
