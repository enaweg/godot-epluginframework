#if TOOLS
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Internal.Kinds;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Installing an add-on that is not in the project yet from a ZIP file chosen in the ePlugin Manager (see
/// <see cref="AddonInstaller"/>); a ZIP file of an installed plugin is installed as an update instead.
/// </summary>
internal sealed partial class EGlobal
{
    private string AddonsRoot => ProjectSettings.GlobalizePath(_host.AddonsDirectory);

    /// <summary>Extracts and validates the add-on of a ZIP file in a new transaction folder, off the editor thread.</summary>
    internal async Task<(ValidatedPackage Package, string Directory)> StageNewAddonAsync(AddonArchive archive, CancellationToken ct)
    {
        if (_fetchingUpdate || _updateJournals is null || _updateJournals.Read().Any(j => j.IsActive)) throw new InvalidOperationException("An update is already in progress.");
        _fetchingUpdate = true;
        var directory = _updateJournals.NewDirectory();
        try
        {
            // Validating a GDExtension needs the editor's version, which only the editor thread may ask Godot for.
            _ = GDExtensionConfig.EditorVersion;
            var target = new PluginUpdateTarget(archive.Slug, archive.Name, "", null, AddonInstaller.Target(AddonsRoot, archive.Slug),
                _stateStore?.IsBlocked(archive.Slug) == true, Kind: archive.IsExtensionAddon ? PluginKind.GDExtensionAddon : PluginKind.Unknown);
            var package = await Task.Run(() => AddonInstaller.Stage(archive, target, directory, ct), ct);
            return (package, directory);
        }
        catch
        {
            AddonFiles.DeleteTree(directory);
            throw;
        }
        finally { _fetchingUpdate = false; }
    }

    /// <summary>
    /// Moves a staged new add-on into <c>res://addons</c>, builds the project when it has C# code (a successful build is
    /// loaded by the editor's assembly reload) and scans the file system. The add-on stays disabled.
    /// </summary>
    internal AddonInstallResult InstallNewAddon(ValidatedPackage package, string directory)
    {
        if (IsChangingPlugins) throw new InvalidOperationException("Finish plugin transitions before installing a plugin.");
        if (_fetchingUpdate || _updateJournals?.Read().Any(j => j.IsActive) == true) throw new InvalidOperationException("An update is in progress; install the plugin once it finished.");
        if (_stateStore?.IsBlocked(package.Candidate.Slug) == true)
            throw new InvalidOperationException("Resolve the plugin's problem under Needs attention in the ePlugin Manager first.");
        if (package.ContainsCSharp && GetOrCreateContext(_ePluginContext!).Cli is not ICheckedDotnetCli)
            throw new InvalidOperationException("A working dotnet CLI is required to install a plugin with C# code.");
        using var progress = ActivationProgress.Begin($"Installing {package.Candidate.PluginName}...");
        return new AddonInstaller(AddonsRoot, _host.ProjectRoot, new GodotAddonInstallHost(this)).Install(package, directory);
    }

    private sealed class GodotAddonInstallHost(EGlobal global) : IAddonInstallHost
    {
        public BuildOutcome Build()
        {
            ActivationProgress.SetText("Building the project with the new plugin...");
            return (global.GetOrCreateContext(global._ePluginContext!).Cli as ICheckedDotnetCli)?.TryBuild() ?? new(1, ["Checked dotnet CLI is unavailable."]);
        }

        public void Scan() => global._host.ScanFilesystem();
        public void Log(string message) => global._ePluginContext?.Logger.Log(message);
    }
}
#endif
