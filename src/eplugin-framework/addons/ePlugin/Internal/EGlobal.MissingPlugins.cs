#if TOOLS
using System.IO;
using System.Linq;
using Godot;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    // Engine metadata survives assembly reloads but not a restart, so the missing plugins are reviewed once per start.
    private const string MissingPluginsMeta = "eplugin_missing_plugins_reviewed";

    /// <summary>Calls <see cref="ReviewMissingPlugins"/> at the first initialization of an editor start.</summary>
    internal void ReviewMissingPluginsOncePerStart()
    {
        if (Engine.Singleton.HasMeta(MissingPluginsMeta)) return;
        Engine.Singleton.SetMeta(MissingPluginsMeta, true);
        ReviewMissingPlugins();
    }

    /// <summary>
    /// Plugins recorded as activated whose folder is gone, e.g. deleted while the editor was closed: Godot reports such a
    /// plugin once and drops it from the enabled plugins. The first start that finds one marks its entry as missing, the
    /// next start removes the entry if it is still missing, and one that turned up again loses the mark. Plugins with an
    /// unfinished attempt or update are left to those.
    /// </summary>
    internal void ReviewMissingPlugins()
    {
        if (_stateStore is not { IsReadOnly: false, HasSharedFile: true } store) return;
        var owned = _updateJournals?.OwnedSlugs;
        var reviewed = store.SharedStates.Where(s => !store.IsBlocked(s.Slug) && owned?.Contains(s.Slug) != true).ToArray();
        var gone = reviewed.Where(s => s.State == PersistedPluginState.Activated && IsMissingPlugin(s.Slug)).ToArray();
        var forget = gone.Where(s => s.Missing).Select(s => s.Slug).ToArray();
        var missing = gone.Where(s => !s.Missing).Select(s => s.Slug).ToArray();
        var found = reviewed.Where(s => s.Missing && !gone.Contains(s)).Select(s => s.Slug).ToArray();
        if (forget.Length + missing.Length + found.Length == 0) return;

        if (!store.TryUpdateMissing(forget, missing, found))
        {
            _ePluginContext?.Logger.Warn("Cannot record missing plugins in the plugin state; they are reviewed again at the next editor start.");
            return;
        }

        foreach (var slug in forget)
        {
            _ePluginContext?.Logger.Log($"Plugin {slug} is still missing; it was removed from the plugin state.");
        }

        foreach (var slug in missing)
        {
            _ePluginContext?.Logger.Warn(
                $"Plugin {slug} is recorded as activated but its folder is missing; it is removed from the plugin state if it is still missing at the next editor start.");
        }
    }

    /// <summary>Whether neither a plugin nor a GDExtension add-on is installed in the plugin's folder.</summary>
    private bool IsMissingPlugin(string slug)
    {
        var directory = ProjectSettings.GlobalizePath(_host.PluginDirectory(slug));
        return !File.Exists(Path.Combine(directory, "plugin.cfg")) && !IsExtensionAddon(slug);
    }
}
#endif
