#if TOOLS
using System;

namespace Enaweg.Plugin;

/// <summary>
/// Builds the installation/uninstallation recipe for an editor plugin.
/// </summary>
/// <remarks>
/// The builder is passed to <see cref="IEEditorPlugin.CreateRecipe"/> and accumulates all
/// resources the plugin requires. The framework applies the resulting recipe when the plugin
/// is activated and reverses it when the plugin is deactivated.
/// <para>
/// This is the root recipe builder: in addition to the resources declared through
/// <see cref="IEEditorPluginRecipeBuilder"/> it declares plugin dependencies. Only the root recipe may do
/// so — the nested recipes of optional dependencies cannot declare further dependencies.
/// </para>
/// All methods return the builder itself to support a fluent call chain.
/// </remarks>
public interface IEEditorPluginBuilder : IEEditorPluginRecipeBuilder
{
    /// <inheritdoc cref="IEEditorPluginRecipeBuilder.AddAutoload"/>
    new IEEditorPluginBuilder AddAutoload(string name, string path);

    /// <inheritdoc cref="IEEditorPluginRecipeBuilder.AddProject(string, bool)"/>
    new IEEditorPluginBuilder AddProject(string path, bool addReference = true);

    /// <inheritdoc cref="IEEditorPluginRecipeBuilder.AddProject(string, string, bool)"/>
    new IEEditorPluginBuilder AddProject(string path, string? virtualFolderName = null, bool addReference = true);

    /// <inheritdoc cref="IEEditorPluginRecipeBuilder.AddNugets"/>
    new IEEditorPluginBuilder AddNugets(params string[] nugetNames);

    /// <inheritdoc cref="IEEditorPluginRecipeBuilder.AddNuget"/>
    new IEEditorPluginBuilder AddNuget(string nugetName, string? version = null, string? source = null);

    /// <inheritdoc cref="IEEditorPluginRecipeBuilder.AddDirectory"/>
    new IEEditorPluginBuilder AddDirectory(string path);

    /// <summary>
    /// Sets the plugin's license. It is shown in the ePlugin Manager and, when <c>plugin.cfg</c> sets
    /// <c>license_required=true</c>, has to be accepted before the plugin is enabled. It replaces the
    /// <c>license_file</c> of <c>plugin.cfg</c> and the <c>LICENSE</c> file of the plugin directory.
    /// </summary>
    /// <param name="license">
    /// Either the license text, which may contain BBCode, or the <c>res://</c> path of the file holding it
    /// (e.g. <c>$"{this.GetPluginDirectory()}/EULA.txt"</c>). A value starting with <c>res://</c> is a path.
    /// </param>
    /// <remarks>
    /// Acceptance is tracked by the path, or for a text by its contents: changing the path or the text asks for the
    /// license to be accepted again. A license set here is only known once the plugin's code is compiled, so an update
    /// that changes it is asked about after it is installed: declining then disables the plugin.
    /// </remarks>
    /// <returns>The builder itself.</returns>
    IEEditorPluginBuilder SetLicense(string license);

    /// <summary>
    /// Declares a dependency on another plugin (C# or GDScript). The framework enables the
    /// dependency before activating this plugin and, when a version constraint is given, verifies
    /// the installed dependency satisfies it.
    /// </summary>
    /// <param name="pluginSlug">
    /// The directory name of the required plugin under <c>addons/</c>
    /// (e.g. <c>"my-dependency"</c> for <c>addons/my-dependency/</c>).
    /// </param>
    /// <param name="version">
    /// Optional version constraint matched against the dependency's <c>plugin.cfg</c>.
    /// Use an exact version (<c>"1.2.3"</c>) or a "<c>&gt;</c>"-prefixed value
    /// (<c>"&gt;1.2.0"</c>) which is interpreted as "this version or higher".
    /// Versions must follow <c>[major].[minor].[patch]</c>; semver pre-release suffixes are
    /// stripped before comparison. When <see langword="null"/>, any installed version is accepted.
    /// </param>
    /// <returns>The builder itself.</returns>
    IEEditorPluginBuilder AddPluginDependency(string pluginSlug, string? version = null);

    /// <summary>
    /// Declares an optional dependency on another plugin together with a nested recipe that is only
    /// installed when that plugin is already enabled.
    /// </summary>
    /// <param name="pluginSlug">
    /// The directory name of the optional plugin under <c>addons/</c>
    /// (e.g. <c>"my-dependency"</c> for <c>addons/my-dependency/</c>).
    /// </param>
    /// <param name="version">
    /// Optional version constraint matched against the dependency's <c>plugin.cfg</c>, using the same
    /// syntax as <see cref="AddPluginDependency"/>. When the installed version does not satisfy it, the
    /// nested recipe is skipped. When <see langword="null"/>, any installed version is accepted.
    /// </param>
    /// <param name="optionalRecipe">
    /// Callback that declares the resources to install when the optional dependency is satisfied. Like
    /// <see cref="IEEditorPlugin.CreateRecipe"/> it must be deterministic and side-effect free.
    /// </param>
    /// <remarks>
    /// Unlike <see cref="AddPluginDependency"/> this never enables the named plugin and never fails this
    /// plugin's activation: when the plugin is not enabled, or its version does not match, the nested
    /// recipe is simply skipped.
    /// <para>
    /// Activation order does not matter: enabling the optional plugin later applies the nested recipe at that
    /// point, and disabling it again reverses the nested recipe while leaving this plugin active.
    /// </para>
    /// </remarks>
    /// <returns>The builder itself.</returns>
    IEEditorPluginBuilder AddOptionalPluginDependency(string pluginSlug, string? version,
        Action<IEEditorPluginRecipeBuilder> optionalRecipe);
}

#endif
