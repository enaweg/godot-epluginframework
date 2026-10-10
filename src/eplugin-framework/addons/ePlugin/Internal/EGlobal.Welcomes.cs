#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Welcomes;
using Godot;

namespace Enaweg.Plugin.Internal;

internal sealed partial class EGlobal
{
    private const int WelcomeCheckFrames = 30;
    private readonly List<WelcomeEntry> _pendingWelcomes = [];
    // Plugins checked this session that have no welcome page, so their files are not read again every check.
    private readonly HashSet<string> _welcomeChecked = new(StringComparer.Ordinal);
    private ulong _nextWelcomeCheck;

    /// <summary>
    /// No plugin is being enabled, disabled or updated, so the welcome pages of the installed plugins can be shown.
    /// </summary>
    private bool WelcomeQuiet => _transition is null && _toCheckEnable.Count == 0 && _toCheckDisable.Count == 0 &&
                                 _recipeUpdateJournal is null && _pendingLicenseReview is null;

    /// <summary>
    /// The welcome pages of enabled plugins that were not shown for the project yet. Called every frame; looks for new
    /// plugins every <see cref="WelcomeCheckFrames"/> frames, which also covers plugins enabled in Project Settings,
    /// installed after an assembly reload, or enabled while the editor was closed.
    /// </summary>
    internal IReadOnlyList<WelcomeEntry>? TakeWelcomes()
    {
        var frame = Engine.GetProcessFrames();
        if (frame < _nextWelcomeCheck)
        {
            return null;
        }

        _nextWelcomeCheck = frame + WelcomeCheckFrames;
        if (_stateStore is null || _stateStore.IsReadOnly || !LicenseUiAvailable || !WelcomeQuiet)
        {
            return null;
        }

        CollectWelcomes(_stateStore);
        if (_pendingWelcomes.Count == 0)
        {
            return null;
        }

        var welcomes = _pendingWelcomes.ToArray();
        _pendingWelcomes.Clear();
        return welcomes;
    }

    private void CollectWelcomes(PluginStateStore store)
    {
        var ready = GetEnabledPluginSlugs()
            .Where(slug => !_welcomeChecked.Contains(slug) && !store.IsWelcomeShown(slug) && !store.IsBlocked(slug) &&
                           _pendingWelcomes.All(w => w.Slug != slug) && IsInstalled(slug))
            .Order(StringComparer.Ordinal).ToArray();
        // read from disk only when there is something to show
        if (ready.Length == 0 || _updateJournals?.Read().Any(j => j.IsActive) == true)
        {
            return;
        }

        foreach (var slug in ready)
        {
            _welcomeChecked.Add(slug);
            if (ReadInstalledWelcome(slug, RecipeOf(slug)) is { } welcome)
            {
                _pendingWelcomes.Add(welcome);
            }
        }
    }

    /// <summary>
    /// An enabled plugin is installed: an ePlugin once its recipe is (never while it is in error or its editor
    /// instance is not loaded yet), the framework and plain plugins as soon as they are enabled.
    /// </summary>
    private bool IsInstalled(string slug)
    {
        if (slug == FrameworkSlug)
        {
            return true;
        }

        var context = _contexts.FirstOrDefault(c => c.Slug == slug && c.Plugin is not null);
        return context?.State == EEditorPluginState.Activated || context is null && !IsManagedPlugin(slug);
    }

    /// <summary>
    /// The welcome page of a plugin: the one its recipe sets, else the welcome_file of its plugin.cfg, else its README.
    /// Null when it has none or plugin.cfg cannot be read.
    /// </summary>
    private static WelcomeEntry? ReadInstalledWelcome(string slug, EEditorPluginRecipe? recipe)
    {
        var directory = $"res://addons/{slug}";
        using var config = new ConfigFile();
        if (config.Load(directory + "/plugin.cfg") != Error.Ok)
        {
            return null;
        }

        string Value(string key) => config.GetValue("plugin", key, "").AsString();
        var name = Value("name");
        var version = Value("version");
        return recipe?.PluginWelcome switch
        {
            { Path: { } path } => PluginWelcome.FromFile(slug, name, version, PluginLicense.SourceOfPath(slug, path),
                ProjectSettings.GlobalizePath(path)),
            { Text: { } text } => PluginWelcome.FromText(slug, name, version, text),
            _ => PluginWelcome.FromConfig(slug, name, version, Value(PluginWelcome.FileKey), ProjectSettings.GlobalizePath(directory))
        };
    }

    /// <summary>The welcome page the ePlugin Manager links in a plugin's details; null when the plugin has none.</summary>
    internal WelcomeEntry? DescribeWelcome(string slug) => ReadInstalledWelcome(slug, RecipeOf(slug));

    /// <summary>Records the welcome pages of a closed welcome dialog as shown, so they are not shown again.</summary>
    internal void CompleteWelcomes(IEnumerable<WelcomeEntry> shown)
    {
        var now = DateTimeOffset.UtcNow;
        var welcomes = shown.Select(w => new ShownWelcome(w.Slug, w.Version, now)).ToArray();
        if (_stateStore is null || welcomes.Length == 0 || _stateStore.TryRecordWelcomes(welcomes))
        {
            return;
        }

        _ePluginContext?.Logger.Warn(
            "Shown welcome pages could not be saved to addons/eplugin-state.json; they are shown again after the editor restarts.");
    }
}
#endif
