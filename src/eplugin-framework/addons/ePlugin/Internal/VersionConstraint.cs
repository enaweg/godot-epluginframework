#if TOOLS
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// The version constraint of a plugin dependency: an exact version (<c>1.2.3</c>), or a minimum written <c>&gt;=1.2.3</c>
/// or, as before, <c>&gt;1.2.3</c>, which also means "this version or higher". Versions are read like update versions
/// (<see cref="SemVer"/>: an optional <c>v</c>, two to four numbers, a prerelease suffix and build metadata), and only
/// their numbers are compared, so <c>1.2</c> equals <c>1.2.0</c> and <c>1.2.3-beta.1</c> satisfies <c>1.2.3</c>.
/// </summary>
internal static class VersionConstraint
{
    /// <summary>Whether <paramref name="version"/> satisfies <paramref name="constraint"/>; false when either is malformed.</summary>
    /// <param name="error">Why it could not be compared, or null.</param>
    public static bool Matches(string? version, string? constraint, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(constraint))
        {
            error = "version or constraint is empty";
            return false;
        }

        var text = constraint.Trim();
        var minimum = text.StartsWith(">=", System.StringComparison.Ordinal) ? 2 : text.StartsWith('>') ? 1 : 0;
        if (!SemVer.TryParse(version, out var given))
        {
            error = $"'{version}' is not a version (needed: [major].[minor][.patch][.revision])";
            return false;
        }

        if (!SemVer.TryParse(text[minimum..], out var required))
        {
            error = $"'{constraint}' is not a version constraint (needed: [>=][major].[minor][.patch][.revision])";
            return false;
        }

        var comparison = Numbers(given).CompareTo(Numbers(required));
        return minimum > 0 ? comparison >= 0 : comparison == 0;
    }

    /// <summary>Whether <paramref name="constraint"/> can be compared with versions.</summary>
    public static bool IsValid(string? constraint)
    {
        var text = constraint?.Trim() ?? "";
        var minimum = text.StartsWith(">=", System.StringComparison.Ordinal) ? 2 : text.StartsWith('>') ? 1 : 0;
        return SemVer.TryParse(text[minimum..], out _);
    }

    private static SemVer Numbers(SemVer version) => new(version.Major, version.Minor, version.Patch, null, version.Revision);
}
#endif
