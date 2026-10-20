#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// GDExtension add-ons: a GDExtension in its own folder below <c>res://addons</c> without a <c>plugin.cfg</c>. Godot loads
/// it whether anything is enabled or not, so for ePlugin it is always available, as a dependency too, and never toggled.
/// Its files name no version: the version is the one the shared plugin state recorded when an update installed it, and
/// unknown until then. Such an add-on is offered no update automatically; the ePlugin Manager installs a chosen version.
/// </summary>
internal sealed partial class EGlobal
{
    /// <summary>Whether <paramref name="slug"/> is a GDExtension add-on, see <see cref="PluginKinds.IsExtensionAddon"/>.</summary>
    internal bool IsExtensionAddon(string slug) =>
        slug.Length > 0 && PluginKinds.IsExtensionAddon(ProjectSettings.GlobalizePath(_host.PluginDirectory(slug)));

    /// <summary>
    /// Whether a plugin a recipe depends on is there for it: enabled, or a GDExtension add-on, which Godot always runs.
    /// </summary>
    private bool IsDependencyAvailable(string slug) => _host.IsPluginEnabled(slug) || IsExtensionAddon(slug);

    /// <summary>The version ePlugin recorded for a GDExtension add-on; null when it did not install it.</summary>
    private string? ExtensionAddonVersion(string slug) => _stateStore?.GetShared(slug)?.Version;

    /// <summary>The slugs of the GDExtension add-ons below the addons folder.</summary>
    private string[] ExtensionAddonSlugs()
    {
        using var addons = DirAccess.Open(_host.AddonsDirectory);
        return (addons?.GetDirectories() ?? []).Where(IsExtensionAddon).ToArray();
    }

    /// <summary>A GDExtension add-on as the ePlugin Manager lists it.</summary>
    private PluginInfo ExtensionAddonInfo(string slug)
    {
        var known = KnownPluginOf(slug);
        return new PluginInfo(slug, known?.Name ?? slug, PluginKind.GDExtensionAddon, true)
        {
            Version = ExtensionAddonVersion(slug) ?? "", UpdateSite = UpdateSiteOf(slug), Known = known,
            FailedAttempt = _stateStore?.GetLocal(slug),
            Extensions = NativeExtensionStatus.Read(_host.ProjectRoot, slug, _host.IsExtensionLoaded)
        };
    }

    /// <summary>A GDExtension add-on as an update target. Without a recorded version only a chosen version can be installed.</summary>
    private PluginUpdateTarget ExtensionAddonTarget(string slug) =>
        new(slug, KnownPluginOf(slug)?.Name ?? slug, ExtensionAddonVersion(slug) ?? "", null,
            ProjectSettings.GlobalizePath(_host.PluginDirectory(slug)), _stateStore?.IsBlocked(slug) == true, _stateStore?.IsReadOnly != false,
            ExtensionAddonVersion(slug), UpdateSiteOf(slug), KnownPluginOf(slug)?.UpdateUrl, PluginKind.GDExtensionAddon);
}
#endif
