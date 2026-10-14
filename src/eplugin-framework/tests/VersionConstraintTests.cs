using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class VersionConstraintTests
{
    [TestCase]
    public void ConstraintsCompareTheNumbersOfUpdateVersions()
    {
        (string Version, string Constraint, bool Matches)[] cases =
        [
            ("1.2.3", "1.2.3", true),
            ("1.2.3", "1.2.4", false),
            ("2.0.0", ">1.0.0", true),
            ("1.0.0", ">1.0.0", true), // ">" has always meant "this version or higher"
            ("1.0.0", ">=1.0.0", true),
            ("0.9.0", ">=1.0.0", false),
            ("1.2", "1.2.0", true),
            ("1.2.0", "1.2", true),
            ("1.2", ">1.2.0", true),
            ("v1.2.3", "1.2.3", true),
            ("1.2.3", "v1.2.3", true),
            ("1.2.3+build.5", "1.2.3", true),
            ("1.2.3-beta.1", "1.2.3", true),
            ("1.2.3", ">=1.2.3-rc.1", true),
            ("0.11.0.3", ">0.11.0.2", true),
            ("0.11.0.3", "0.11.0", false),
            (" 1.2.3 ", " >= 1.2.3 ", true),
        ];

        foreach (var (version, constraint, matches) in cases)
        {
            var actual = VersionConstraint.Matches(version, constraint, out var error);
            Assertions.AssertBool(actual).OverrideFailureMessage($"{version} against {constraint}").IsEqual(matches);
            Assertions.AssertObject(error).OverrideFailureMessage($"{version} against {constraint}: {error}").IsNull();
        }
    }

    [TestCase]
    public void MalformedVersionsAndConstraintsNeverMatchAndSayWhy()
    {
        foreach (var (version, constraint) in new[]
                 {
                     ("not-a-version", "1.0.0"), ("1.0.0", "not-a-version"), ("1.0.0", ">not-a-version"),
                     ("", "1.0.0"), ("1.0.0", ""), ("1", "1.0.0"), ("1.0.0", ">")
                 })
        {
            var actual = VersionConstraint.Matches(version, constraint, out var error);
            Assertions.AssertBool(actual).OverrideFailureMessage($"{version} against {constraint}").IsFalse();
            Assertions.AssertString(error).IsNotEmpty();
        }
    }
}
