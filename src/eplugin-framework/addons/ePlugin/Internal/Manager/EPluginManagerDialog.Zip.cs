#if TOOLS
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Install from ZIP: a ZIP file of a plugin that is not in the project yet is installed as a new add-on after its own
/// confirmation (see <see cref="ZipInstall"/>); one of an installed plugin is installed like a version chosen in its
/// details.
/// </summary>
internal sealed partial class EPluginManagerDialog
{
    private void ChooseZip()
    {
        if (Busy) return;
        _zipDialog.PopupCentered();
    }

    private void ZipChosen(string path) => Run(InstallZipAsync(path), "Installing from a ZIP file");

    /// <summary>
    /// Reads the ZIP file and either stages its add-on and asks before installing it, or asks before installing it over
    /// the installed plugin. Tests call it directly, as only the file dialog chooses a file.
    /// </summary>
    internal async Task InstallZipAsync(string path)
    {
        if (Busy) return;
        _operationNotice = null;
        var file = Path.GetFileName(path);
        _working = true; Buttons(); _status.Text = $"Reading {file}...";
        try { await ReadZipAsync(path, file); }
        // whatever failed, the manager must not stay locked
        finally { _working = _installing = false; }
    }

    private async Task ReadZipAsync(string path, string file)
    {
        AddonArchive archive;
        try { archive = await Task.Run(() => AddonArchive.Read(path), _lifetime); }
        catch (OperationCanceledException) { _working = false; return; }
        // any file can be chosen, so reading it may fail in more ways than a damaged ZIP file
        catch (Exception ex)
        {
            _working = false;
            if (IsShown) ShowZipNotice(new(InstallStep.Failed, $"Cannot install {file}: {ex.Message}", true));
            return;
        }
        if (!IsShown) { _working = false; return; }
        var sameExceptCase = _model.Plugins.FirstOrDefault(p => p.Plugin.Slug != archive.Slug &&
                                                                p.Plugin.Slug.Equals(archive.Slug, StringComparison.OrdinalIgnoreCase))?.Plugin.Slug;
        var choice = ZipInstall.Choose(archive, _model.Find(archive.Slug), _targets.FirstOrDefault(t => t.Slug == archive.Slug), sameExceptCase);
        if (!choice.IsNew)
        {
            _working = false;
            if (choice.Version is { } version && _model.Find(archive.Slug) is { } row)
            {
                // The installed plugin's details show what the confirmation is about.
                _selectedSlug = archive.Slug;
                Render();
                ConfirmVersion(row, version);
            }
            else ShowZipNotice(new(InstallStep.Failed, $"Cannot install {file}: {choice.Refused}", true));
            return;
        }
        _installing = true; _cancel?.Dispose(); _cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Render();
        InstallOutcome outcome;
        try
        {
            outcome = await _updates.StageAddonAsync(archive, new InstallView
            {
                Alive = () => !_lifetime.IsCancellationRequested && IsShown,
                Status = text => _status.Text = text
            }, _cancel.Token);
        }
        finally { _working = _installing = false; }
        if (!IsShown || outcome.Step == InstallStep.Abandoned) return;
        if (outcome.Step != InstallStep.Staged || _updates.StagedAddon is not { } package)
        {
            ShowZipNotice(outcome);
            return;
        }
        _installText.Text = ZipInstall.Describe(package, archive);
        _status.Text = $"Confirm the installation of {archive.Name}.";
        Buttons();
        _installConfirm.PopupCentered();
    }

    private void ConfirmAddon()
    {
        if (_updates.StagedAddon is null || !BeginPluginOperation())
        {
            CancelAddon();
            return;
        }
        _status.Text = $"Installing {_updates.StagedAddon.Candidate.PluginName}...";
        // Not inside the confirmation's signal: installing may build the project.
        CallDeferred(MethodName.InstallAddon);
    }

    private void CancelAddon()
    {
        if (_updates.StagedAddon is null || _pluginOperation.IsRunning) return;
        _updates.Clear();
        if (IsShown) ShowZipNotice(new(InstallStep.Canceled, "Installation canceled. Addon files were not changed."));
    }

    private void InstallAddon()
    {
        // An assembly reload replaced this dialog since the click; the staged add-on is gone with it.
        if (!IsInitialized || !_pluginOperation.IsRunning) return;
        var slug = _updates.StagedAddon?.Candidate.Slug;
        InstallOutcome outcome;
        try { outcome = _updates.StagedAddon is null ? new(InstallStep.Canceled) : _updates.InstallAddon(); }
        catch (Exception ex) { outcome = new(InstallStep.Failed, "Installing failed: " + ex.Message, true, true); }
        _operationNotice = outcome.Notice;
        _keepNotice = !Visible;
        if (outcome.IsError) GD.PushError(outcome.Notice);
        // The new plugin is selected in the list read again, unless the build's assembly reload replaces this dialog.
        if (outcome.Step == InstallStep.Installed) _selectedSlug = slug;
        CompletePluginOperation();
    }

    private void ShowZipNotice(InstallOutcome outcome)
    {
        _operationNotice = outcome.Notice;
        _keepNotice = !Visible;
        if (outcome.IsError) GD.PushError(outcome.Notice);
        Render();
    }
}
#endif
