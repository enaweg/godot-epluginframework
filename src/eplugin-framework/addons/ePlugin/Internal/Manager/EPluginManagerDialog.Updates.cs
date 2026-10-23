#if TOOLS
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Licenses;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Checking for updates and installing the checked ones; <see cref="UpdateFlow"/> does the work and the dialog shows
/// its outcome.
/// </summary>
internal sealed partial class EPluginManagerDialog
{
    private static Task<bool> ReviewUpdates(UpdateReview review)
    {
        // continue the installation after the dialog's signal, not inside it
        var decided = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateReviewDialog.Create(review, decided.SetResult).Open();
        return decided.Task;
    }

    private static Task<LicenseReview> ReviewLicenses(LicenseReview review)
    {
        // continue the installation after the dialog's signal, not inside it
        var decided = new TaskCompletionSource<LicenseReview>(TaskCreationOptions.RunContinuationsAsynchronously);
        LicenseDialog.Create(review, decided.SetResult).Open();
        return decided.Task;
    }

    private void CheckNow() => Run(CheckAsync(), "Checking for updates");

    private async Task CheckAsync()
    {
        if (Busy) return;
        _operationNotice = null;
        _working = true; Buttons(); _status.Text = "Checking for updates...";
        // Remote sites and local ZIPs can take a while; the scope ends after the list is read again.
        using var progress = ActivationProgress.Begin("Checking for updates...");
        CheckOutcome outcome;
        try { outcome = await _updates.CheckAsync(); }
        finally { _working = false; }
        if (!IsShown) return;
        _operationNotice = outcome.Notice;
        _keepNotice = !Visible;
        if (outcome.IsError) GD.PushError(outcome.Notice);
        Refresh();
    }

    private void Confirm()
    {
        if (_model.CanApply) Install(new InstallRequest(_model.Selected));
    }

    private void Install(InstallRequest request) => Run(InstallAsync(request), "Installing updates");

    /// <summary>
    /// Stages and validates the batch, has the user review packages that need a decision in their own dialog, then
    /// installs.
    /// </summary>
    private async Task InstallAsync(InstallRequest request)
    {
        if (Busy || request.Batch.Count == 0) return;
        _operationNotice = null;
        _working = _installing = true; _cancel?.Dispose(); _cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        Render();
        InstallOutcome outcome;
        try
        {
            outcome = await _updates.InstallAsync(request, new InstallView
            {
                Alive = () => !_lifetime.IsCancellationRequested && IsShown,
                Status = text => _status.Text = text,
                Swapping = () => { _swapping = true; Buttons(); }
            }, _cancel.Token);
        }
        finally { _working = _installing = _swapping = false; }
        if (!IsShown || outcome.Step == InstallStep.Abandoned) return;
        // The build-failure dialog takes over the decision. Otherwise the manager stays open: an update that needs no
        // restart is finished here, and a restart reopens it.
        if (outcome.Step == InstallStep.AwaitingDecision) Hide();
        else
        {
            _operationNotice = outcome.Notice;
            _keepNotice = !Visible;
            if (outcome.IsError) GD.PushError(outcome.Notice);
        }
        if (outcome.Refresh) Refresh(); else Render();
    }

    private void Cancel()
    {
        if (_swapping || _pluginOperation.IsBusy || _global.IsChangingPlugins || AssemblyReload.IsPending) return;
        _cancel?.Cancel();
        if (!_working) _updates.Clear();
        Hide();
    }
}
#endif
