#if TOOLS
namespace Enaweg.Plugin.Internal;

/// <summary>The ePlugin Framework as a plugin of the project.</summary>
internal static class Framework
{
    /// <summary>
    /// Its folder below <c>res://addons</c>, which is its slug: the implicit dependency of every ePlugin's recipe, and the
    /// plugin whose update restarts the editor and is recovered early.
    /// </summary>
    public const string Slug = "ePlugin";
}
#endif
