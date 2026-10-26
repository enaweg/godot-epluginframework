#if TOOLS
using System;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Cleanup of the local directories: deleting old plugin packages from one, and suggesting it when one holds more than
/// the user's limit. The suggestion is checked like the scheduled update check, once per editor start when that check is
/// due, and shown by <see cref="EPluginPlugin"/> once no plugin is changing.
/// </summary>
internal sealed partial class EGlobal
{
    private bool _cleanupCheckDue;

    /// <inheritdoc cref="LocalSourceConfig.CleanupWarningGigabytes"/>
    internal double CleanupWarningGigabytes => _localSources?.CleanupWarningGigabytes ?? LocalSourceSettings.DefaultCleanupWarningGigabytes;

    /// <summary>Saves <see cref="CleanupWarningGigabytes"/> with the user's local directories.</summary>
    /// <exception cref="InvalidOperationException">The list is read-only, see <see cref="LocalDirectoriesProblem"/>.</exception>
    internal bool SetCleanupWarning(double gigabytes) => ChangeLocalDirectories(settings => settings.SetCleanupWarning(gigabytes));

    /// <summary>
    /// Deletes the ZIP files of <paramref name="plan"/> in <paramref name="directory"/> off the editor thread, then
    /// indexes again. Completes on the editor thread's context when awaited from it.
    /// </summary>
    internal async Task<CleanupResult> CleanUpLocalDirectoryAsync(string directory, CleanupPlan plan)
    {
        var result = await Task.Run(() => LocalCleanup.Delete(directory, plan.Remove));
        _ePluginContext?.Logger.Log($"Local directory cleanup of {directory}: deleted {result.Deleted} plugin package{(result.Deleted == 1 ? "" : "s")}, " +
                                    $"{LocalCleanup.FormatSize(result.Bytes)}.");
        foreach (var failure in result.Failures) _ePluginContext?.Logger.Warn($"Cannot delete {failure.Path}: {failure.Message}");
        await RebuildLocalIndexAsync();
        return result;
    }

    /// <summary>Checks the sizes once the startup indexing and update check are done.</summary>
    internal void ScheduleCleanupCheck() => Post(() => _cleanupCheckDue = true);

    /// <summary>
    /// The local directory to suggest a cleanup for, at most once per scheduled check; null while plugins change or
    /// dialogs cannot be shown, or when every directory is within the limit.
    /// </summary>
    internal CleanupSuggestion? TakeCleanupSuggestion()
    {
        if (!_cleanupCheckDue || IsChangingPlugins || IsIndexingLocalSources || !LicenseUiAvailable) return null;
        _cleanupCheckDue = false;
        var suggestion = LocalCleanup.OverLimit(LocalIndex, LocalDirectories, CleanupWarningGigabytes);
        if (suggestion is not null)
            _ePluginContext?.Logger.Log($"The local directory {suggestion.Directory} holds {LocalCleanup.FormatSize(suggestion.Bytes)} of plugin packages, " +
                                        $"more than the {LocalCleanup.FormatSize(suggestion.Limit)} set for a cleanup.");
        return suggestion;
    }
}
#endif
