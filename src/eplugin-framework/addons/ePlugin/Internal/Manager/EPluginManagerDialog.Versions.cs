#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// The selected plugin's published versions, loaded from its update site, and installing one of them.
/// </summary>
internal sealed partial class EPluginManagerDialog
{
    /// <summary>
    /// The version choice of an updatable plugin: the versions in the local directories, plus those of its
    /// update_url once they are loaded. Without the update site's versions the local ones are still offered.
    /// </summary>
    private void RenderVersions(PluginRow? row)
    {
        var visible = row is { IsUpdatable: true, Plugin.Missing: false };
        _versionSeparator.Visible = _versionRow.Visible = visible;
        _versionSelect.Clear(); _versionOptions = []; _versionNote = null;
        var target = visible ? _targets.FirstOrDefault(t => t.Slug == row!.Plugin.Slug) : null;
        if (!visible) { }
        else if (!row!.Plugin.Enabled || target is null) SetVersionPlaceholder(PluginManagerViewModel.VersionText(row.Plugin) + " (installed)", "Enable the plugin to change its version.");
        else
        {
            var local = _global.ListLocalVersions(target);
            // Until the update site's versions are listed, or when they cannot be (e.g. a rate limit), an add-on of unknown
            // version offers the latest release a check found.
            IReadOnlyList<UpdateCandidate> remote = _updates.RemoteVersions.GetValueOrDefault(target.Slug)
                ?? (_global.UpdateCache is { } cache && UpdateScheduler.CachedLatest(cache, target) is { } cached ? [cached] : []);
            string? missing = null;
            if (target.UpdateUrls.Count > 0 && !_updates.RemoteVersions.ContainsKey(target.Slug))
            {
                if (_updates.RemoteVersionErrors.TryGetValue(target.Slug, out var error)) missing = "Versions from the update site are unavailable: " + error;
                else { missing = "Loading versions from the update site..."; Run(LoadVersionsAsync(target), "Loading versions"); }
            }
            if (local.Count == 0 && remote.Count == 0 && missing is not null)
            {
                var loading = _updates.IsLoadingVersions(target.Slug);
                SetVersionPlaceholder(loading ? "Loading versions..." : PluginManagerViewModel.VersionText(row.Plugin) + " (installed)", loading ? null : missing);
            }
            else
            {
                _versionOptions = PluginManagerViewModel.VersionOptions(row.Plugin.Version, UpdateService.MergeVersions(local, remote));
                var latest = _versionOptions.FirstOrDefault(o => !o.Installed && o.Candidate is not null && !o.IsDowngrade);
                for (var i = 0; i < _versionOptions.Count; i++)
                {
                    var option = _versionOptions[i];
                    _versionSelect.AddItem(PluginManagerViewModel.VersionLabel(option, option == latest), i);
                    if (PluginManagerViewModel.VersionSource(option) is { } source) _versionSelect.SetItemTooltip(i, source);
                    if (option.Installed) _versionSelect.Select(i);
                }
                // A GDExtension add-on of unknown version lists no installed version; the latest is preselected instead.
                var installed = _versionOptions.Any(o => o.Installed);
                if (!installed && latest is not null) _versionSelect.Select(_versionOptions.ToList().IndexOf(latest));
                _versionSelect.Disabled = Busy || installed && _versionOptions.Count < 2 || _versionOptions.Count == 0;
                _versionNote = missing;
            }
        }
        VersionButtons();
    }

    private void SetVersionPlaceholder(string text, string? note)
    {
        _versionSelect.AddItem(text); _versionSelect.Select(0); _versionSelect.Disabled = true;
        _versionNote = note;
    }

    private async Task LoadVersionsAsync(PluginUpdateTarget target)
    {
        if (!await _updates.LoadVersionsAsync(target, _lifetime) || !IsShown) return;
        var slug = target.Slug;
        // A newer listed version is now a pending update; read the list again so the plugin shows it. Never discard a
        // staged batch awaiting review, interrupt work or read the plugins while they change: that waits until it ended.
        var pending = _global.PendingUpdates.FirstOrDefault(c => c.Slug == slug);
        var changed = pending?.NewVersion != _model.Find(slug)?.Update?.Candidate.NewVersion;
        if (changed && !Busy) Refresh();
        else
        {
            if (changed) _refreshWhenIdle = true;
            if (_selectedSlug == slug) RenderVersions(_model.Find(slug));
        }
    }

    private VersionOption? SelectedVersion()
    {
        var index = _versionSelect.Selected;
        return index >= 0 && _versionSelect.GetItemId(index) is var id && id >= 0 && id < _versionOptions.Count && _versionOptions.Count > 0
            ? _versionOptions[id] : null;
    }

    private void VersionButtons()
    {
        var row = _selectedSlug is null ? null : _model.Find(_selectedSlug);
        var option = _versionOptions.Count == 0 ? null : SelectedVersion();
        if (row is null || option is null)
        {
            _installVersion.Disabled = true; _installVersion.Text = "Update"; _installVersion.Icon = EditorIcons.Upgrade;
            ShowVersionHint(_versionNote);
            return;
        }
        var blocked = PluginManagerViewModel.VersionChangeBlocked(row, option);
        _installVersion.Text = option.Action; _installVersion.Icon = VersionIcon(option.IsDowngrade, option.IsReinstall);
        _installVersion.Disabled = blocked is not null || Busy;
        _installVersion.TooltipText = blocked ?? (option.IsReinstall
            ? $"Install version {option.Version} of {row.Plugin.Name} again, replacing its files."
            : $"Install version {option.Version} of {row.Plugin.Name}.");
        ShowVersionHint((option.Installed ? null : blocked) ?? _versionNote);
    }

    private void ShowVersionHint(string? hint) { _versionHint.Text = hint ?? ""; _versionHint.Visible = hint is not null; }

    private void ConfirmVersion()
    {
        var row = _selectedSlug is null ? null : _model.Find(_selectedSlug);
        var option = SelectedVersion();
        if (row is null || option?.Candidate is null || PluginManagerViewModel.VersionChangeBlocked(row, option) is not null || Busy) return;
        ConfirmVersion(row, option);
    }

    /// <summary>Asks before installing <paramref name="option"/>, a listed version or a ZIP file chosen with Install from ZIP.</summary>
    private void ConfirmVersion(PluginRow row, VersionOption option)
    {
        if (option.Candidate is null) return;
        // The listed candidate was built when the list was loaded; the installed version may have changed since.
        _pendingVersion = option.Candidate with { InstalledVersion = row.Plugin.Version };
        _pendingDowngrade = option.IsDowngrade; _pendingReinstall = option.IsReinstall;
        _versionConfirm.OkButtonText = option.Action; _versionConfirm.GetOkButton().Icon = VersionIcon(option.IsDowngrade, option.IsReinstall);
        _versionConfirm.DialogText = (option.IsReinstall
                ? $"Reinstall {row.Plugin.Name} {row.Plugin.Version}?\n\n{PluginManagerViewModel.VersionSource(option)}. Its files replace the installed ones: " +
                  "this brings in new commits of a git branch and undoes changes made to the plugin's folder in the project.\n\n"
                : option.ReplacesUnknown
                ? $"Install {row.Plugin.Name} {option.Version}?\n\n{PluginManagerViewModel.VersionSource(option)}. Its files replace the installed ones, " +
                  "whose version ePlugin does not know because it did not install them. From then on, updates are found.\n\n"
                : $"Replace {row.Plugin.Name} {row.Plugin.Version} with version {option.Version}?\n\n") +
            (option.IsDowngrade
                ? "Use this to undo an update that broke the project. Project code or saved data that already relies on the newer version may stop working. "
                : "") +
            "Open scenes are saved first; files and project references are backed up" + _global.DetailsBuildFailurePolicy(row.Plugin.Slug) switch
            {
                "ask" => ". If the project does not build with it, you choose whether to keep it and fix the errors yourself or to restore the previous version.",
                "keep" => ". If the project does not build with it, it is kept so you can fix the errors (project setting eplugin/updates/on_build_failure).",
                _ => " and restored if installation fails.",
            };
        _versionConfirm.PopupCentered();
    }
}
#endif
