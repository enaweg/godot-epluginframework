#if TOOLS
using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Enaweg.Plugin.Internal.Update;

internal readonly record struct SemVer(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<SemVer>
{
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (text is null) return false;
        var match = Regex.Match(text.Trim(), @"^[vV]?(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Success ? match.Groups[3].Value : "0", out var patch)) return false;
        var pre = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (pre?.Split('.').Any(x => x.All(char.IsAsciiDigit) && x.Length > 1 && x[0] == '0') == true) return false;
        version = new SemVer(major, minor, patch, pre);
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
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

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease is null ? "" : "-" + Prerelease);
}
#endif
