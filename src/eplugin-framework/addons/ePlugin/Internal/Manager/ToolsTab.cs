#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// The Tools tab of the settings. Its one tool cleans up a local directory: it shows what the index found in the
/// selected directory, deletes old plugin packages by version or by date, and sets the size above which a cleanup is
/// suggested. The layout lives in SettingsDialog.tscn, which also holds the confirmation.
/// </summary>
[Tool]
internal sealed partial class ToolsTab : VBoxContainer, ISerializationListener
{
    private const int PluginColumn = 0, PackagesColumn = 1, VersionsColumn = 2, SizeColumn = 3, LatestColumn = 4;
    private const int PreviewFiles = 20;
    private readonly EditorSignalConnections _signals = new();
    private EGlobal _global = null!;
    private OptionButton _directory = null!;
    private Label _statistics = null!;
    private Tree _tree = null!;
    private OptionButton _versionRule = null!;
    private Button _versionButton = null!;
    private Label _versionPreview = null!;
    private OptionButton _dateRule = null!;
    private Button _dateButton = null!;
    private Label _datePreview = null!;
    private SpinBox _warning = null!;
    private Label _status = null!;
    private ConfirmationDialog _confirm = null!;
    private string? _selected;
    private CleanupPlan _versionPlan = CleanupPlan.Empty, _datePlan = CleanupPlan.Empty;
    // The cleanup the confirmation asks for, planned for the directory it names.
    private (string Directory, CleanupPlan Plan)? _pending;
    private bool _cleaning;
    private string? _message, _messageDetails;
    // The local index changed while the tab was hidden or plugins were changing; it is rendered once it is shown again.
    private bool _stale;

    public void Initialize(EGlobal global)
    {
        _global = global;
        _directory = GetNode<OptionButton>("%CleanupDirectory"); _statistics = GetNode<Label>("%CleanupStatistics");
        _tree = GetNode<Tree>("%CleanupTree");
        _versionRule = GetNode<OptionButton>("%CleanupVersionRule"); _versionButton = GetNode<Button>("%CleanupVersionButton");
        _versionPreview = GetNode<Label>("%CleanupVersionPreview");
        _dateRule = GetNode<OptionButton>("%CleanupDateRule"); _dateButton = GetNode<Button>("%CleanupDateButton");
        _datePreview = GetNode<Label>("%CleanupDatePreview");
        _warning = GetNode<SpinBox>("%CleanupWarningSpin"); _status = GetNode<Label>("%CleanupStatus");
        _confirm = GetNode<ConfirmationDialog>("%CleanupConfirm");
        // Column titles and sizing are not scene properties of Tree.
        string[] titles = ["Plugin", "Packages", "Versions", "Size", "Latest"];
        for (var column = 0; column < titles.Length; column++)
        {
            _tree.SetColumnTitle(column, titles[column]);
            if (column == PluginColumn) continue;
            _tree.SetColumnExpand(column, false);
            _tree.SetColumnCustomMinimumWidth(column, (int)(100 * EditorWindows.Scale));
        }
        // The rules are an enum, so their items are too.
        AddRules(_versionRule, LocalCleanup.VersionRules);
        AddRules(_dateRule, LocalCleanup.DateRules);
        _signals.Connect(_directory, OptionButton.SignalName.ItemSelected, Callable.From<long>(index =>
        {
            _selected = _directory.GetItemMetadata((int)index).AsString();
            _message = null;
            Render();
        }));
        _signals.Connect(_versionRule, OptionButton.SignalName.ItemSelected, Callable.From<long>(_ => Render()));
        _signals.Connect(_dateRule, OptionButton.SignalName.ItemSelected, Callable.From<long>(_ => Render()));
        _signals.Connect(_versionButton, BaseButton.SignalName.Pressed, Callable.From(() => Confirm(_versionPlan, Rule(_versionRule))));
        _signals.Connect(_dateButton, BaseButton.SignalName.Pressed, Callable.From(() => Confirm(_datePlan, Rule(_dateRule))));
        _signals.Connect(_confirm, AcceptDialog.SignalName.Confirmed, Callable.From(CleanUp));
        _signals.Connect(_warning, Godot.Range.SignalName.ValueChanged, Callable.From<double>(SaveWarning));
    }

    private static void AddRules(OptionButton option, CleanupRule[] rules)
    {
        option.Clear();
        foreach (var rule in rules)
        {
            option.AddItem(LocalCleanup.Describe(rule), (int)rule);
        }
        option.Select(0);
    }

    private static CleanupRule Rule(OptionButton option) => (CleanupRule)option.GetSelectedId();

    /// <summary>Reads the index again; with <paramref name="directory"/>, selects that local directory.</summary>
    public void Open(string? directory = null)
    {
        _message = null; _messageDetails = null;
        if (directory is not null) _selected = directory;
        Render();
    }

    public void Render()
    {
        if (!GodotObject.IsInstanceValid(this)) return;
        _stale = false;
        var directories = _global.LocalDirectories;
        if (_selected is null || !directories.Contains(_selected, PackageFiles.PathComparer)) _selected = directories.Count > 0 ? directories[0] : null;
        _directory.Clear();
        foreach (var directory in directories)
        {
            _directory.AddItem(directory);
            _directory.SetItemMetadata(_directory.ItemCount - 1, directory);
            if (PackageFiles.PathComparer.Equals(directory, _selected)) _directory.Select(_directory.ItemCount - 1);
        }
        _directory.Disabled = directories.Count == 0;
        _directory.TooltipText = _selected ?? "Add local directories on the Update sources tab.";
        _warning.SetValueNoSignal(_global.CleanupWarningGigabytes);
        _warning.Editable = _global.LocalDirectoriesProblem is null;

        var packages = _selected is null ? [] : _global.LocalIndex.In(_selected).ToArray();
        _statistics.Text = Statistics(packages);
        var plugins = _global.CollectPlugins();
        var names = plugins.ToDictionary(p => p.Slug, p => p.Name, StringComparer.Ordinal);
        RenderTree(packages, names);
        // The versions this project has installed are kept, whatever the rule.
        var installed = plugins.Where(p => !p.Missing && p.Version.Length > 0).Select(p => (p.Slug, p.Version)).ToArray();
        var now = DateTime.UtcNow;
        _versionPlan = LocalCleanup.Plan(packages, Rule(_versionRule), now, installed);
        _datePlan = LocalCleanup.Plan(packages, Rule(_dateRule), now, installed);
        Preview(_versionPreview, _versionPlan);
        Preview(_datePreview, _datePlan);
        var busy = _cleaning || _global.IsIndexingLocalSources || _selected is null;
        _versionButton.Disabled = busy || _versionPlan.Remove.Count == 0;
        _dateButton.Disabled = busy || _datePlan.Remove.Count == 0;
        RenderStatus(packages.Sum(p => p.Length));
    }

    private string Statistics(LocalPackage[] packages)
    {
        if (_selected is null) return "No local directory yet. Add one on the Update sources tab.";
        var state = _global.LocalIndex.StateOf(_selected);
        if (state is null) return _global.IsIndexingLocalSources ? "Indexing..." : "Not indexed yet.";
        if (!state.Exists) return "This directory was not found when it was indexed.";
        if (packages.Length == 0) return _global.IsIndexingLocalSources ? "Indexing..." : "No plugin packages found in this directory.";
        var statistics = LocalCleanup.Statistics(packages);
        var prereleases = statistics.Prereleases == 0 ? "" : $", {Count(statistics.Prereleases, "prerelease")}";
        return $"{Count(statistics.Packages, "plugin package")} of {Count(statistics.Plugins, "plugin")} " +
               $"({Count(statistics.Versions, "version")}{prereleases}), {LocalCleanup.FormatSize(statistics.Bytes)}. " +
               $"Files from {statistics.OldestUtc!.Value.ToLocalTime():yyyy-MM-dd} to {statistics.NewestUtc!.Value.ToLocalTime():yyyy-MM-dd}.";
    }

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private void RenderTree(LocalPackage[] packages, Dictionary<string, string> names)
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        foreach (var plugin in LocalCleanup.PerPlugin(packages))
        {
            var item = _tree.CreateItem(root);
            var name = names.GetValueOrDefault(plugin.Slug);
            item.SetText(PluginColumn, name ?? plugin.Slug);
            item.SetTooltipText(PluginColumn, name is null ? $"{plugin.Slug} is not in res://addons." : $"{name} (res://addons/{plugin.Slug})");
            if (name is null) item.SetCustomColor(PluginColumn, EditorIcons.DisabledColor);
            item.SetText(PackagesColumn, plugin.Packages.ToString(CultureInfo.InvariantCulture));
            item.SetText(VersionsColumn, plugin.Versions.ToString(CultureInfo.InvariantCulture));
            item.SetText(SizeColumn, LocalCleanup.FormatSize(plugin.Bytes));
            item.SetText(LatestColumn, plugin.Latest);
            for (var column = 0; column < _tree.Columns; column++)
            {
                item.SetSelectable(column, false);
                if (column != PluginColumn) item.SetTextAlignment(column, HorizontalAlignment.Right);
            }
        }
    }

    /// <summary>What a cleanup would delete, with the files it names in the tooltip.</summary>
    private void Preview(Label label, CleanupPlan plan)
    {
        label.Text = plan.Remove.Count == 0 ? "Nothing to delete"
            : $"Deletes {Count(plan.Remove.Count, "file")}, {LocalCleanup.FormatSize(plan.Bytes)}";
        var files = plan.Remove.OrderBy(p => p.ZipPath, StringComparer.OrdinalIgnoreCase)
            .Select(p => _selected is null ? p.ZipPath : Path.GetRelativePath(_selected, p.ZipPath)).ToArray();
        label.TooltipText = files.Length == 0 ? "" : string.Join("\n", files.Take(PreviewFiles)) +
            (files.Length > PreviewFiles ? $"\nand {files.Length - PreviewFiles} more" : "");
    }

    private void RenderStatus(long bytes)
    {
        _status.TooltipText = _messageDetails ?? "";
        var limit = (long)(_global.CleanupWarningGigabytes * LocalCleanup.Gigabyte);
        _status.Text = _message ?? _global.LocalDirectoriesProblem
            ?? (_cleaning ? "Deleting plugin packages..."
                : limit > 0 && bytes > limit ? $"This directory holds more than the {LocalCleanup.FormatSize(limit)} above which a cleanup is suggested."
                : "");
    }

    private void Confirm(CleanupPlan plan, CleanupRule rule)
    {
        if (_selected is null || plan.Remove.Count == 0) return;
        _pending = (_selected, plan);
        _confirm.DialogText = $"Delete {Count(plan.Remove.Count, "plugin package")} ({LocalCleanup.FormatSize(plan.Bytes)}) from {_selected}?\n\n" +
                              $"Kept: {LocalCleanup.Describe(rule).ToLowerInvariant()}, the latest version of every plugin and the versions " +
                              "installed in this project. The files are deleted, not moved to the trash.";
        _confirm.PopupCentered();
    }

    private void CleanUp()
    {
        if (_pending is not { } pending || _cleaning) return;
        _pending = null;
        if (_global.IsChangingPlugins)
        {
            _message = "Plugins are being changed; try again once they are done.";
            Render();
            return;
        }
        _cleaning = true;
        _message = null; _messageDetails = null;
        Render();
        ObservedTask.Run(CleanUpAsync(pending.Directory, pending.Plan), ex => GD.PushError($"Cleaning up {pending.Directory} failed: {ex.Message}"));
    }

    private async Task CleanUpAsync(string directory, CleanupPlan plan)
    {
        try
        {
            var result = await _global.CleanUpLocalDirectoryAsync(directory, plan);
            _message = $"Deleted {Count(result.Deleted, "plugin package")}, {LocalCleanup.FormatSize(result.Bytes)}." +
                       (result.Failures.Count == 0 ? "" : $" {result.Failures.Count} could not be deleted (hover for details).");
            _messageDetails = result.Failures.Count == 0 ? null : string.Join("\n", result.Failures.Select(f => $"{f.Path}: {f.Message}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _message = "Cannot clean up the directory: " + ex.Message;
        }
        finally
        {
            _cleaning = false;
            if (GodotObject.IsInstanceValid(this)) Render();
        }
    }

    private void SaveWarning(double gigabytes)
    {
        try
        {
            _global.SetCleanupWarning(gigabytes);
            _message = null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _message = "Cannot save the size: " + ex.Message.Split(" (Parameter")[0];
        }
        Render();
    }

    /// <summary>
    /// Renders the new index only while the tab is shown, and not while plugins change: reading the plugins records plain
    /// plugins in the shared state file.
    /// </summary>
    private void LocalIndexChanged()
    {
        if (CanRender) Render();
        else _stale = true;
    }

    private bool CanRender => GodotObject.IsInstanceValid(this) && IsVisibleInTree() && !_global.IsChangingPlugins;

    public override void _Process(double delta)
    {
        base._Process(delta);
        if (_stale && _global is not null && CanRender) Render();
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        if (_global is not null) _global.LocalIndexChanged += LocalIndexChanged;
    }

    private void ReleaseCallbacks()
    {
        _signals.Dispose();
        if (_global is not null) _global.LocalIndexChanged -= LocalIndexChanged;
    }

    // Reload serializes every script before disposing any of them, so callback targets are still valid here.
    public void OnBeforeSerialize() => ReleaseCallbacks();
    public void OnAfterDeserialize() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseCallbacks();
        base.Dispose(disposing);
    }

    public override void _ExitTree()
    {
        ReleaseCallbacks();
        base._ExitTree();
    }
}
#endif
