#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal.Kinds;

/// <summary>What a kind's validation sees of a staged package: the package's files and the installed plugin's.</summary>
internal sealed record KindValidation(ValidationContext Context, IReadOnlyList<string> StagedFiles, IReadOnlyList<string> InstalledFiles);

/// <summary>One plugin's files were just swapped in or restored; <paramref name="Discarded"/> files were still in use.</summary>
internal sealed record SwappedPlugin(IUpdateHost Host, UpdateJournal Journal, UpdatePluginJournal Plugin, string Installed, int Discarded);

/// <summary>
/// The steps of ePlugin's workflows that differ between <see cref="PluginKind"/>s. Every kind has its own sealed
/// implementation, so what happens to a plugin of that kind (how its update is validated, how its files are replaced,
/// whether it is disabled meanwhile and what has to happen once its files changed) is in one place, and every decision is
/// logged with the kind's name. <see cref="UpdateApplier"/> runs the steps in the same order for every kind.
/// </summary>
internal abstract class PluginKindWorkflow
{
    public abstract PluginKind Kind { get; }

    /// <summary>Shown in the ePlugin Manager and prefixed to every log line of the workflow.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Whether an update disables the plugin while it replaces its files and enables it again afterwards. ePlugins stay
    /// enabled: their recipes are reconciled instead of uninstalled and installed again.
    /// </summary>
    public abstract bool DisabledDuringSwap { get; }

    /// <summary>How an update replaces the plugin's folder.</summary>
    public virtual IAddonSwap Swap => AddonFolderSwap.Instance;

    /// <summary>Findings of this kind about a staged package, added to the general rules of <see cref="AddonPackageValidator"/>.</summary>
    public virtual IEnumerable<Finding> Validate(KindValidation validation) => [];

    /// <summary>Right before the plugin's files are replaced; what it records is saved with the journal.</summary>
    public virtual void BeforeSwap(UpdatePluginJournal plugin) { }

    /// <summary>After the new files are in place, before the editor scans the file system or builds.</summary>
    public virtual void AfterSwap(SwappedPlugin swapped) { }

    /// <summary>After a rollback put the old files back.</summary>
    public virtual void AfterRestore(SwappedPlugin restored) { }

    /// <summary>Whether this editor has to restart to load the plugin's files as they are now, apart from any C# build.</summary>
    public virtual bool NeedsRestart(UpdatePluginJournal plugin) => false;

    protected void Trace(IUpdateHost host, UpdatePluginJournal plugin, string message) => host.Log($"{Name} {plugin.Slug}: {message}");

    /// <summary>R10: a GDExtension in a kind whose updates cannot replace one.</summary>
    protected static Finding? RefuseNativeExtension(KindValidation validation) => RefuseNativeExtension(validation.StagedFiles.Concat(validation.InstalledFiles));

    /// <summary>R10 when <paramref name="files"/> contain a GDExtension, which updates of ePlugins cannot replace yet.</summary>
    public static Finding? RefuseNativeExtension(IEnumerable<string> files) =>
        files.Any(GDExtensionConfig.IsExtensionFile)
            ? new("R10", FindingSeverity.Error, "ePlugins with a GDExtension are not supported by ePlugin updates yet; only plugins without a recipe can ship one.")
            : null;
}
#endif
