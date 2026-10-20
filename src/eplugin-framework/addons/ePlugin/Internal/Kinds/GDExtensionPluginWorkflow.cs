#if TOOLS
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>
/// What every kind whose update replaces a GDExtension without restarting into a new assembly shares
/// (<see cref="GDExtensionPluginWorkflow"/> and <see cref="GDExtensionAddonWorkflow"/>). Godot loads the extension
/// whether the plugin is enabled or not, so an update has to replace native libraries the editor uses:
/// <list type="bullet">
/// <item>On Windows the editor loads each library from a copy named <c>~&lt;library&gt;</c> in the same folder and keeps it
/// open, so the folder cannot be moved. The files are replaced one by one (<see cref="AddonFileSwap"/>), leaving the
/// runtime copies alone; elsewhere the original is loaded, and deleting rather than overwriting it keeps it mapped.</item>
/// <item>Extensions that are <c>reloadable</c> in their old and new versions are reloaded in place
/// (<see cref="IUpdateHost.ReloadExtension"/>); any other change (an extension added, removed or not reloadable, a library
/// still in use) keeps the old library loaded until the editor restarts, which the update then requests.</item>
/// </list>
/// An ePlugin that ships a GDExtension stays an <see cref="EPluginWorkflow"/>, which swaps its files the same way but
/// restarts anyway. GDExtensions in recipe directories (<c>AddDirectory</c>) must be reloadable, see
/// <see cref="ShowHideHelper"/>.
/// </summary>
internal abstract class NativeExtensionWorkflow : PluginKindWorkflow
{
    public override IAddonSwap Swap(UpdatePluginJournal plugin) => AddonFileSwap.Instance;

    /// <summary>
    /// Identifies this editor process, so a journal tells whether the libraries it changed were loaded by this editor or
    /// by one that started after them.
    /// </summary>
    internal static string EditorSession { get; set; } = CurrentProcess();

    private static string CurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        return $"{System.Environment.ProcessId}:{process.StartTime.ToUniversalTime().Ticks}";
    }

    public override IEnumerable<Finding> Validate(KindValidation validation)
    {
        foreach (var finding in GDExtensionRules.Validate(validation)) yield return finding;
        var before = GDExtensionConfig.ReadAll(validation.Context.Installed.Directory);
        var after = GDExtensionConfig.ReadAll(validation.Context.StagingDir);
        if (GDExtensionConfig.ReloadsInPlace(before, after))
            yield return new("R21", FindingSeverity.Info, "The GDExtension is reloaded in place; the editor does not restart for it.");
        else
            yield return new("R21", FindingSeverity.Info, "The editor restarts after the update to load the new GDExtension libraries: " + RestartReason(before, after) + ".");
    }

    private static string RestartReason(IReadOnlyDictionary<string, GDExtensionConfig?> before, IReadOnlyDictionary<string, GDExtensionConfig?> after)
    {
        if (before.Count == 0) return "the plugin had no GDExtension before";
        if (after.Count == 0) return "the new version has no GDExtension";
        if (before.Keys.Any(k => !after.ContainsKey(k)) || after.Keys.Any(k => !before.ContainsKey(k))) return "its .gdextension files change";
        return "the extension is not reloadable (reloadable=true in its [configuration])";
    }

    public override void BeforeSwap(UpdatePluginJournal plugin)
    {
        plugin.NativeSession = EditorSession;
        plugin.ReloadedInPlace = false;
        plugin.RestartRequired = false;
    }

    public override void AfterSwap(SwappedPlugin swapped)
    {
        var (host, journal, plugin, installed, discarded) = swapped;
        var before = GDExtensionConfig.ReadAll(journal.Backup(plugin.Slug));
        var after = GDExtensionConfig.ReadAll(installed);
        Trace(host, plugin, $"replaced the files, keeping Godot's runtime copies; extensions before: {Describe(before)}, after: {Describe(after)}.");
        if (discarded > 0)
            RequireRestart(host, plugin, $"{discarded} old file(s) were still in use and were moved aside");
        else if (!GDExtensionConfig.ReloadsInPlace(before, after))
            RequireRestart(host, plugin, RestartReason(before, after));
        else if (Reload(host, plugin, after.Keys))
            plugin.ReloadedInPlace = true;
        else
            RequireRestart(host, plugin, "Godot could not reload the extension in place");
    }

    public override void AfterRestore(SwappedPlugin restored)
    {
        var (host, _, plugin, installed, discarded) = restored;
        // Without a reload or a restart since the swap, this editor still runs the libraries that were restored.
        var runsNew = plugin.ReloadedInPlace || plugin.NativeSession != EditorSession;
        plugin.NativeSession = EditorSession;
        plugin.ReloadedInPlace = false;
        plugin.RestartRequired = false;
        if (!runsNew && discarded == 0)
        {
            Trace(host, plugin, "restored the old files; the editor still runs their libraries.");
            return;
        }
        var configs = GDExtensionConfig.ReadAll(installed);
        if (discarded > 0)
            RequireRestart(host, plugin, $"{discarded} file(s) of the new version were still in use and were moved aside");
        else if (configs.Count == 0 || configs.Values.Any(c => c is not { Reloadable: true }))
            RequireRestart(host, plugin, "the editor runs the new libraries and the restored extension is not reloadable");
        else if (Reload(host, plugin, configs.Keys))
            Trace(host, plugin, "restored the old files and reloaded their libraries in place.");
        else
            RequireRestart(host, plugin, "Godot could not reload the restored extension in place");
    }

    public override bool NeedsRestart(UpdatePluginJournal plugin) => plugin.RestartRequired && plugin.NativeSession == EditorSession;

    private bool Reload(IUpdateHost host, UpdatePluginJournal plugin, IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var path = $"res://addons/{plugin.Slug}/{file}";
            if (!host.ReloadExtension(path)) return false;
            Trace(host, plugin, $"reloaded {path} in place.");
        }
        return true;
    }

    private void RequireRestart(IUpdateHost host, UpdatePluginJournal plugin, string reason)
    {
        plugin.RestartRequired = true;
        Trace(host, plugin, $"the editor restarts to load its libraries: {reason}.");
    }

    private static string Describe(IReadOnlyDictionary<string, GDExtensionConfig?> configs) => configs.Count == 0 ? "none"
        : string.Join(", ", configs.Select(c => c.Key + (c.Value is null ? " (unreadable)" : c.Value.Reloadable ? " (reloadable)" : " (not reloadable)")));
}

/// <summary>
/// A plugin without a recipe that ships a GDExtension next to its <c>plugin.cfg</c>. Its entry script is GDScript or C#;
/// a C# one is built like a C# plugin's. It is disabled while its files are replaced; see
/// <see cref="NativeExtensionWorkflow"/> for how they are.
/// </summary>
internal sealed class GDExtensionPluginWorkflow : NativeExtensionWorkflow
{
    public static readonly GDExtensionPluginWorkflow Instance = new();
    public override PluginKind Kind => PluginKind.GDExtension;
    public override string Name => "GDExtension plugin";
    public override bool DisabledDuringSwap => true;
}

/// <summary>
/// A GDExtension in its own folder below <c>res://addons</c> without a <c>plugin.cfg</c>. Godot loads it and nothing
/// enables it, so nothing is disabled while its files are replaced; see <see cref="NativeExtensionWorkflow"/> for how
/// they are. Its version is not in its files: the update records the installed version in the shared plugin state.
/// </summary>
internal sealed class GDExtensionAddonWorkflow : NativeExtensionWorkflow
{
    public static readonly GDExtensionAddonWorkflow Instance = new();
    public override PluginKind Kind => PluginKind.GDExtensionAddon;
    public override string Name => "GDExtension add-on";
    public override bool DisabledDuringSwap => false;
}
#endif
