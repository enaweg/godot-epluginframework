#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Licenses;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Enabling and disabling plugins from the list, and the decisions of the Needs attention dialog. An operation locks
/// the manager until it ended and, when it rebuilt the project, until the editor loaded the rebuilt code.
/// </summary>
internal sealed partial class EPluginManagerDialog
{
    /// <summary>Carries out a decision made in the Needs attention dialog, which shows the list again afterwards.</summary>
    private void AttentionRequested(AttentionAction action, AttentionItem? item)
    {
        switch (action)
        {
            case AttentionAction.RetryAll: RetryFailed(null, "Retrying failed plugins..."); break;
            case AttentionAction.Retry when item is not null: RetryFailed([item.Slug], $"Retrying {item.Plugin.Name}..."); break;
            case AttentionAction.Rollback when item?.Kept is { } kept:
                if (!BeginPluginOperation()) return;
                try { _global.RollbackKeptUpdate(kept); }
                finally { CompletePluginOperation(); }
                break;
            case AttentionAction.Disable when item is not null: DisableFailed(item.Slug, item.Plugin.Name); break;
        }
    }

    private void DisableFailed(string slug, string name)
    {
        if (!BeginPluginOperation()) return;
        _status.Text = "Disabling " + name + "...";
        bool handled;
        try
        {
            using (ActivationProgress.Begin("Disabling " + name + "..."))
            {
                handled = _global.DisableFailedPlugin(slug);
                // a plain plugin's dependants and optional recipes
                if (handled) _global.ObservePluginToggles();
            }
        }
        catch (Exception ex)
        {
            GD.PushError($"Cannot disable {slug}: {ex.Message}");
            handled = true;
        }
        // Without a failed attempt of its own it is disabled like from the list, outside this signal.
        if (handled) CompletePluginOperation();
        else CallDeferred(MethodName.SetPluginEnabled, slug, name, false);
    }

    private void ItemEdited() => CellEdited(_tree.GetEdited(), _tree.GetEditedColumn());

    /// <summary>
    /// A check box of the list was clicked: selects an update for the batch, or enables or disables the plugin. Tests
    /// call it directly, as only a click sets the tree's edited cell.
    /// </summary>
    internal void CellEdited(TreeItem? item, int column)
    {
        if (!IsInitialized) return;
        var row = SlugOf(item) is { } slug ? _model.Find(slug) : null;
        if (item is null || row is null) return;
        if (Busy)
        {
            // Undo the click; the tree is rebuilt outside its own edit signal.
            if (_reloadPending) _status.Text = ReloadNotice;
            CallDeferred(MethodName.Render);
            return;
        }
        if (column == VersionColumn && row.Update is { } update)
        {
            update.Selected = item.IsChecked(VersionColumn);
            Buttons();
        }
        else if (column == EnabledColumn)
        {
            var enable = item.IsChecked(EnabledColumn);
            if (!enable && row.Plugin.Kind == PluginKind.Framework)
            {
                item.SetChecked(EnabledColumn, true);
                _disableFrameworkConfirm.PopupCentered();
                return;
            }
            // Enabling or disabling runs the plugin's recipe and may rebuild; never do that, or rebuild the tree,
            // inside the Tree's own edit signal.
            if (!BeginPluginOperation()) return;
            _status.Text = (enable ? "Enabling " : "Disabling ") + row.Plugin.Name + "...";
            // Method calls rather than delegates, which Godot cannot carry over an assembly reload.
            if (enable) CallDeferred(MethodName.EnableAfterLicenses, row.Plugin.Slug, row.Plugin.Name);
            else CallDeferred(MethodName.SetPluginEnabled, row.Plugin.Slug, row.Plugin.Name, false);
        }
    }

    private bool BeginPluginOperation()
    {
        if (Busy || !_pluginOperation.TryBegin()) return false;
        AssemblyReload.Capture();
        _working = true;
        Buttons();
        return true;
    }

    /// <param name="refresh">
    /// Read the list again; false only redraws it, e.g. while an update waits for the reload that continues it.
    /// </param>
    private void CompletePluginOperation(bool refresh = true)
    {
        // A dependency's deferred callback can keep the activation open after SetPluginEnabled returns. _Process completes
        // the operation once it is done, as asked here.
        if (_global.IsChangingPlugins)
        {
            _refreshOnCompletion = refresh;
            _working = false;
            Buttons();
            return;
        }
        _refreshOnCompletion = true;
        _reloadPending = AssemblyReload.IsPending;
        _pluginOperation.Complete(_reloadPending);
        _working = false;
        // A pending reload keeps the old native UI locked. Its replacement is initialized after the reload.
        // Do not reopen it if the timestamp guard times out: another operation would still run on the old code.
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;
        if (refresh) Refresh(); else Render();
    }

    /// <summary>
    /// Asks for the licenses of the plugin and of the dependencies enabled with it first; a declined license leaves the
    /// plugin disabled.
    /// </summary>
    private void EnableAfterLicenses(string slug, string name)
    {
        // An assembly reload replaced this dialog since the click; its successor shows the current state.
        if (!IsInitialized || !_pluginOperation.IsRunning) return;
        LicenseReview? review;
        try { review = _global.ReviewActivationLicenses(slug, name); }
        catch (Exception ex)
        {
            GD.PushError($"Cannot read the licenses of {slug}: {ex.Message}");
            CompletePluginOperation();
            return;
        }
        if (review is null) { SetPluginEnabled(slug, name, true); return; }
        _status.Text = $"Review the license to enable {name}.";
        try { OpenLicenseDialog(slug, name, review); }
        catch (Exception ex)
        {
            GD.PushError($"Cannot show the license of {slug}: {ex.Message}");
            CompletePluginOperation();
        }
    }

    private void OpenLicenseDialog(string slug, string name, LicenseReview review)
    {
        LicenseDialog.Create(review, decided =>
        {
            try { _global.RecordLicenses(decided); }
            catch (Exception ex)
            {
                GD.PushError($"Cannot record the license decisions for {slug}: {ex.Message}");
                CompletePluginOperation();
                return;
            }
            if (decided.Approved.Any()) { CallDeferred(MethodName.SetPluginEnabled, slug, name, true); return; }
            _pluginOperation.Complete(false);
            _working = false;
            if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;
            Refresh();
            _status.Text = $"{name} was not enabled: a license it needs was declined.";
        }).Open();
    }

    private void SetPluginEnabled(string slug, string name, bool enable)
    {
        if (!IsInitialized || !_pluginOperation.IsRunning || EndedByPendingReload()) return;
        // Also covers plain plugins; an ePlugin's own activation shares this window.
        using var progress = ActivationProgress.Begin((enable ? "Enabling " : "Disabling ") + name + "...");
        try
        {
            _global.SetPluginEnabled(slug, enable);
            // a plain plugin's dependants and optional recipes, before the list below shows them
            _global.ObservePluginToggles();
        }
        catch (Exception ex) { GD.PushError($"Cannot {(enable ? "enable" : "disable")} {slug}: {ex.Message}"); }
        finally
        {
            // Dependencies and dependants may have been toggled too, so reload the whole list.
            CompletePluginOperation();
        }
    }

    private void DisableFramework()
    {
        if (!BeginPluginOperation()) return;
        // The framework removes this dialog while it is disabled.
        Hide();
        var slug = _model.Plugins.First(p => p.Plugin.Kind == PluginKind.Framework).Plugin.Slug;
        // Not inside the confirmation's signal: disabling the framework releases this dialog.
        CallDeferred(MethodName.DisableFrameworkNow, slug);
    }

    private void DisableFrameworkNow(string slug)
    {
        // An assembly reload replaced this dialog since the click; nothing is disabled without asking again.
        if (!IsInitialized) return;
        try { _global.SetPluginEnabled(slug, false); }
        catch (Exception ex)
        {
            // still enabled, so this dialog stays and must not stay locked
            GD.PushError($"Cannot disable {slug}: {ex.Message}");
            CompletePluginOperation();
        }
    }

    /// <summary>
    /// Whether the operation ends without starting because a reload became due since it began, e.g. while a license
    /// dialog waited for the user: it would run on the old code. The manager then waits for the reload.
    /// </summary>
    private bool EndedByPendingReload()
    {
        if (!AssemblyReload.IsPending) return false;
        CompletePluginOperation(refresh: false);
        if (GodotObject.IsInstanceValid(this)) _status.Text = ReloadNotice;
        return true;
    }

    /// <param name="slugs">The plugins to retry with what they need; null retries every failed attempt.</param>
    private void RetryFailed(IReadOnlyCollection<string>? slugs, string text)
    {
        if (!BeginPluginOperation()) return;
        try
        {
            using (ActivationProgress.Begin(text)) _global.RetryFailedPlugins(slugs);
        }
        finally { CompletePluginOperation(); }
    }
}
#endif
