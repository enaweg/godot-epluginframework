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
            case AttentionAction.RetryAll:
                RunPluginOperation("Retrying failed plugins...", "Cannot retry the failed plugins", () => Retry(null));
                break;
            case AttentionAction.Retry when item is not null:
                RunPluginOperation($"Retrying {item.Plugin.Name}...", $"Cannot retry {item.Slug}", () => Retry([item.Slug]));
                break;
            case AttentionAction.Rollback when item?.Kept is { } kept:
                RunPluginOperation($"Rolling back the update of {item.Plugin.Name}...", $"Cannot roll back the update of {item.Slug}", () =>
                {
                    _global.RollbackKeptUpdate(kept);
                    return true;
                });
                break;
            case AttentionAction.Disable when item is not null:
                RunPluginOperation($"Disabling {item.Plugin.Name}...", $"Cannot disable {item.Slug}", () => DisableFailed(item.Slug, item.Plugin.Name));
                break;
        }
    }

    /// <summary>
    /// Runs a decision of the Needs attention dialog as a plugin operation, in a progress scope of <paramref name="text"/>,
    /// which the status line shows too. A failure is shown in the error area, starting with <paramref name="failure"/>,
    /// rather than escaping through the dialog's signal, and completes the operation.
    /// </summary>
    /// <param name="work">False when it left the operation to a deferred step, which completes it.</param>
    private void RunPluginOperation(string text, string failure, Func<bool> work)
    {
        if (!BeginPluginOperation()) return;
        ClearNotice();
        _status.Text = text;
        bool complete;
        try
        {
            using (ActivationProgress.Begin(text)) complete = work();
        }
        catch (Exception ex)
        {
            SetNotice($"{failure}: {ex.Message}", true);
            complete = true;
        }
        if (complete) CompletePluginOperation();
    }

    /// <param name="slugs">The plugins to retry with what they need; null retries every failed attempt.</param>
    private bool Retry(IReadOnlyCollection<string>? slugs)
    {
        _global.RetryFailedPlugins(slugs);
        return true;
    }

    private bool DisableFailed(string slug, string name)
    {
        if (_global.DisableFailedPlugin(slug))
        {
            // a plain plugin's dependants and optional recipes
            _global.ObservePluginToggles();
            return true;
        }
        // Without a failed attempt of its own it is disabled like from the list, outside this signal.
        CallDeferred(MethodName.SetPluginEnabled, slug, name, false);
        return false;
    }

    private void ItemEdited() => CellEdited(_tree.GetEdited(), _tree.GetEditedColumn());

    /// <summary>
    /// A check box of the list was clicked: enables or disables the plugin. Tests call it directly, as only a click sets
    /// the tree's edited cell.
    /// </summary>
    internal void CellEdited(TreeItem? item, int column)
    {
        if (!IsInitialized) return;
        var row = SlugOf(item) is { } slug ? _model.Find(slug) : null;
        if (item is null || row is null) return;
        if (PollBusy())
        {
            // Undo the click; the tree is rebuilt outside its own edit signal.
            if (_session.ReloadPending) _status.Text = PluginManagerViewModel.ReloadNotice;
            CallDeferred(MethodName.Render);
            return;
        }
        if (column == EnabledColumn)
        {
            var enable = item.IsChecked(EnabledColumn);
            if (!enable && row.Plugin.Kind == PluginKind.Framework)
            {
                item.SetChecked(EnabledColumn, true);
                EditorWindows.PopupChild(_disableFrameworkConfirm);
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
        if (Busy || !_session.Operation.TryBegin()) return false;
        AssemblyReload.Capture();
        _session.Working = true;
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
            _session.RefreshOnCompletion = refresh;
            _session.Working = false;
            Buttons();
            return;
        }
        _session.RefreshOnCompletion = true;
        _session.Operation.Complete(PollReload());
        _session.Working = false;
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
        if (!IsInitialized || !_session.Operation.IsRunning) return;
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
            if (decided.Approved.Any())
            {
                // Kept in Engine metadata until it is enabled: a reload due before that discards this call, and
                // Initialize enables the plugin then.
                EGlobal.RememberLicenseEnable(slug);
                CallDeferred(MethodName.EnableAccepted, slug, name);
                return;
            }
            _session.Operation.Complete(false);
            _session.Working = false;
            if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;
            Refresh();
            _status.Text = decided.UnreadableReason is { } unreadable
                ? $"{name} was not enabled: {unreadable}."
                : $"{name} was not enabled: a license it needs was declined.";
        }).Open();
    }

    /// <summary>
    /// Enables a plugin whose licenses were just accepted. When a reload became due while the dialog was open, the
    /// enable waits for it like the decisions of a license review, rather than being dropped with the license recorded.
    /// </summary>
    private void EnableAccepted(string slug, string name)
    {
        // An assembly reload replaced this dialog since the click; Initialize enables the plugin as remembered.
        if (!IsInitialized) return;
        if (_session.Operation.IsRunning && !AssemblyReload.IsPending)
        {
            EGlobal.TakeLicenseEnable(slug);
            SetPluginEnabled(slug, name, true);
            return;
        }
        // applied once the reload is done (posted work waits for it), or right away when nothing is pending
        _global.ResumeLicenseDecisions();
        if (_session.Operation.IsRunning && EndedByPendingReload()) _status.Text = $"{name} is enabled once the editor loaded the rebuilt C# code...";
    }

    private void SetPluginEnabled(string slug, string name, bool enable)
    {
        if (!IsInitialized || !_session.Operation.IsRunning || EndedByPendingReload()) return;
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
        if (GodotObject.IsInstanceValid(this)) _status.Text = PluginManagerViewModel.ReloadNotice;
        return true;
    }
}
#endif
