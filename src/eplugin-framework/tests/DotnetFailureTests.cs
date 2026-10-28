#if TOOLS
using Enaweg.Plugin.Internal.Dotnet;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>What a failed dotnet command tells the user: its errors and what to do about the common ones.</summary>
[TestSuite]
public class DotnetFailureTests
{
    [TestCase]
    public static void AnUnreachableSourceKeepsTheErrorAndSuggestsCheckingTheConnection()
    {
        var text = DotnetFailure.Describe(1,
        [
            "  Determining projects to restore...",
            "/p/Game.csproj : error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.",
            "    0 Warning(s)",
            "    1 Error(s)"
        ])!;

        Assertions.AssertString(text).Contains("error NU1301: Unable to load the service index");
        Assertions.AssertString(text).NotContains("1 Error(s)");
        Assertions.AssertString(text).Contains("Check your internet connection, proxy and the sources in nuget.config");
    }

    [TestCase]
    public static void AMissingPackageIsTracedToTheSourcesOrThePluginsAuthor()
    {
        var text = DotnetFailure.Describe(1, ["error: NU1101: Unable to find package Foo.Bar. No packages exist with this id in source(s): nuget.org"])!;
        Assertions.AssertString(text).Contains("Unable to find package Foo.Bar");
        Assertions.AssertString(text).Contains("report it to the plugin's author");
    }

    [TestCase]
    public static void AMissingDotnetCommandSaysHowToMakeItAvailable()
    {
        Assertions.AssertString(DotnetFailure.Describe(127, ["sh: 1: dotnet: not found"])).Contains(DotnetFailure.DotnetMissingHint);
        Assertions.AssertString(DotnetFailure.Describe(-1, [])).IsEqual(DotnetFailure.DotnetMissingHint);
        // another command that exits with 127 is not dotnet's absence
        Assertions.AssertString(DotnetFailure.Describe(127, ["error MSB1009: Project file does not exist."])).NotContains(DotnetFailure.DotnetMissingHint);
    }

    [TestCase]
    public static void ManyErrorsAreShortenedAndOutputWithoutErrorsShowsItsEnd()
    {
        var many = new string[12];
        for (var i = 0; i < many.Length; i++) many[i] = $"a.cs({i},1): error CS0103: The name 'x{i}' does not exist";
        var text = DotnetFailure.Describe(1, many)!;
        Assertions.AssertString(text).Contains("x4'");
        Assertions.AssertString(text).NotContains("x5'");
        Assertions.AssertString(text).Contains("… and 7 more, see the Output panel.");

        Assertions.AssertString(DotnetFailure.Describe(1, ["one", "two", "three", "four"])).IsEqual("two\nthree\nfour");
        Assertions.AssertObject(DotnetFailure.Describe(1, [])).IsNull();
    }
}
#endif
