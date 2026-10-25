#if TOOLS
using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>
/// A plugin version, from a tag or plugin.cfg: an optional "v", two to four numbers (Godot add-ons such as Phantom Camera
/// use a fourth, e.g. 0.11.0.3), then optionally any suffix after a '-' (0.1.2-beta.1, 0.2-anystringhere) and build
/// metadata after a '+'. Versions are compared by their numbers; a suffix marks a prerelease, which comes before the same
/// numbers without one (0.1.2-b1 &lt; 0.1.2), except the suffixes stable, release and final, which some add-ons tag their
/// releases with (1.4.0-stable is 1.4.0). Only suffixes of equal numbers are compared with each other, like semantic
/// prereleases, so 0.1.2-beta.1 &lt; 0.1.2-beta.2. Build metadata is ignored. <see cref="ToString"/> is the version as it
/// was written.
/// </summary>
internal readonly record struct SemVer(int Major, int Minor, int Patch, string? Prerelease = null, int Revision = 0) : IComparable<SemVer>
{
    private static readonly Regex Pattern = new(
        @"^[vV]?(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?)?(?:-([^\s\p{Cc}+]+))?(?:\+([^\s\p{Cc}]+))?$",
        RegexOptions.CultureInvariant);

    /// <summary>The version as it was written, without surrounding whitespace; null when it was not parsed.</summary>
    private string? Text { get; init; }

    /// <summary>
    /// The version without "v" and build metadata and with at least three numbers, so differently written tags of one
    /// version ("v1.2", "1.2.0") have the same key.
    /// </summary>
    public string Key => $"{Major}.{Minor}.{Patch}" + (Revision == 0 ? "" : $".{Revision}") + (Prerelease is null ? "" : "-" + Prerelease);

    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (text is null) return false;
        var match = Pattern.Match(text.Trim());
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Success ? match.Groups[3].Value : "0", out var patch) ||
            !int.TryParse(match.Groups[4].Success ? match.Groups[4].Value : "0", out var revision)) return false;
        var pre = match.Groups[5].Success && !IsStableSuffix(match.Groups[5].Value) ? match.Groups[5].Value : null;
        version = new SemVer(major, minor, patch, pre, revision) { Text = text.Trim() };
        return true;
    }

    private static bool IsStableSuffix(string suffix) =>
        suffix.Equals("stable", StringComparison.OrdinalIgnoreCase) || suffix.Equals("release", StringComparison.OrdinalIgnoreCase) ||
        suffix.Equals("final", StringComparison.OrdinalIgnoreCase);

    // The words a prerelease suffix starts with: 1.0.0-beta1, 4.6.0-dev2, 1.1.0-rc.1, 5.2.0-nightly.4dc91441.
    private static readonly string[] Stages =
        ["alpha", "beta", "rc", "pre", "preview", "prerelease", "dev", "devel", "develop", "nightly", "snapshot", "canary", "test",
         "testing", "experimental", "unstable", "wip"];

    /// <summary>
    /// Whether the suffix names a prerelease stage, such as beta or rc, rather than something else, such as the Godot
    /// version a build is for (9.8.0-4) or where it is published (1.2.0-assetlib).
    /// </summary>
    public bool NamesStage => Prerelease is { } suffix &&
        Stages.Contains(new string(suffix.TakeWhile(char.IsAsciiLetter).ToArray()), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The same numbers without the prerelease suffix, written without it ("v9.8.0-4" becomes "v9.8.0"): the version of
    /// a release its host marks as no prerelease although its tag has a suffix.
    /// </summary>
    public SemVer WithoutPrerelease() => Prerelease is null ? this
        : this with { Prerelease = null, Text = Text?.Remove(Text.IndexOf('-', StringComparison.Ordinal), Prerelease.Length + 1) };

    public int CompareTo(SemVer other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result == 0) result = Revision.CompareTo(other.Revision);
        if (result != 0) return result;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var ln = BigInteger.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var l);
            var rn = BigInteger.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var r);
            result = ln && rn ? l.CompareTo(r) : ln != rn ? (ln ? -1 : 1) : string.CompareOrdinal(left[i], right[i]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }

    public override string ToString() => Text ?? Key;

    /// <summary>
    /// Whether two written versions are the same version ("v1.2", "1.2.0"); text that is no version must be equal. Godot
    /// reads an unquoted <c>version=1.2.0</c> in plugin.cfg as 1.2, the updater's own reader as written.
    /// </summary>
    public static bool Same(string? left, string? right) =>
        TryParse(left, out var l) && TryParse(right, out var r) ? l.CompareTo(r) == 0 : left == right;
}
#endif
