#if TOOLS
using System;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Reading the recipe of an ePlugin without installing it, enabled or not: for its dependencies, license and welcome
/// page, and to order retries.
/// </summary>
internal sealed partial class EGlobal
{
    /// <summary>
    /// The recipe of an ePlugin: from its editor instance, or for a disabled ePlugin from a throw-away instance of its
    /// compiled script type. Null for other plugins and when the recipe cannot be created.
    /// </summary>
    private EEditorPluginRecipe? RecipeOf(string slug)
    {
        var context = ManagedContext(slug);
        if (context is null)
        {
            return ReadRecipeOfDisabledPlugin(slug);
        }

        if (context.IsRecipeCreated)
        {
            return context.Builder.PluginRecipe;
        }

        try
        {
            // a throw-away builder: a failing CreateRecipe must not leave a half-filled recipe on the context
            var builder = EEditorPluginBuilder.Create();
            context.Plugin!.CreateRecipe(builder);
            return builder.PluginRecipe;
        }
        catch (Exception)
        {
            return null; // the activation itself reports the recipe failure
        }
    }

    /// <summary>The recipe the ePlugin Manager summarizes in a plugin's details; null for other plugins.</summary>
    internal EEditorPluginRecipe? DescribeRecipe(string slug) => RecipeOf(slug);

    /// <summary>
    /// A disabled ePlugin has no editor instance, but its script type is compiled. A throw-away instance creates the
    /// recipe, which is deterministic and side-effect free.
    /// </summary>
    private EEditorPluginRecipe? ReadRecipeOfDisabledPlugin(string slug)
    {
        var builder = EEditorPluginBuilder.Create();
        try
        {
            return _host.CreateRecipeOfDisabledPlugin(slug, builder) ? builder.PluginRecipe : null;
        }
        catch (Exception ex)
        {
            _ePluginContext?.Logger.Warn($"Cannot read the recipe of {slug} to collect its license and dependencies: {ex.Message}");
            return null;
        }
    }
}
#endif
