#if TOOLS
using System;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class BuildDiagnosticsTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "build diagnostics", "My Game"));
    private static string At(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string Error(string relative, string code = "CS0117", string message = "'Plug' does not contain a definition for 'Api'") =>
        $"{At(relative)}(2,83): error {code}: {message} [{At("Game.csproj")}]";
    private static BuildDiagnosis Classify(params string[] output) => BuildDiagnostics.Classify(output, Root, fileExists: _ => true);

    [TestCase]
    public static void ErrorsOnlyInProjectFilesAreProjectCode()
    {
        var diagnosis = Classify(Error("Main.cs"), Error("scenes/Player.cs", "CS0618", "'Plug.Old()' is obsolete"), "    0 Warning(s)");
        Assertions.AssertObject(diagnosis.Cause).IsEqual(BuildFailureCause.ProjectCode);
        Assertions.AssertThat(diagnosis.ProjectErrors.Select(e => e.File).ToArray()).ContainsExactly("Main.cs", "scenes/Player.cs");
        Assertions.AssertInt(diagnosis.ProjectErrors[0].Line).IsEqual(2);
        Assertions.AssertInt(diagnosis.ProjectErrors[0].Column).IsEqual(83);
        Assertions.AssertString(diagnosis.ProjectErrors[0].Code).IsEqual("CS0117");
        Assertions.AssertString(diagnosis.ProjectErrors[0].Message).IsEqual("'Plug' does not contain a definition for 'Api'");
    }

    [TestCase]
    public static void AnErrorInPluginCodeMakesItPluginCode()
    {
        Assertions.AssertObject(Classify(Error("Main.cs"), Error("addons/plug/Plug.cs")).Cause).IsEqual(BuildFailureCause.PluginCode);
        // A linked addon reported with the path it points to, or a recipe project outside addons/.
        var linked = Path.GetFullPath(Path.Combine(Root, "..", "plug-source"));
        var output = new[] { $"{Path.Combine(linked, "Plug.cs")}(1,1): error CS1002: ; expected" };
        Assertions.AssertObject(BuildDiagnostics.Classify(output, Root, [linked], _ => true).Cause).IsEqual(BuildFailureCause.PluginCode);
    }

    [TestCase]
    public static void PathCaseFollowsThePlatform()
    {
        var cause = Classify(Error("Addons/plug/Plug.cs")).Cause;
        Assertions.AssertObject(cause).IsEqual(OperatingSystem.IsLinux() ? BuildFailureCause.ProjectCode : BuildFailureCause.PluginCode);
    }

    [TestCase]
    public static void ErrorsWithoutASourceFileAreUnknown()
    {
        foreach (var line in new[]
                 {
                     $"{At("Game.csproj")} : error NU1101: Unable to find package Missing.Package.",
                     "MSBUILD : error MSB1009: Project file does not exist.",
                     "CSC : error CS2001: Source file 'x.cs' could not be found.",
                     $"{At("Game.csproj")}(12,5): error MSB4025: The project file could not be loaded.",
                 })
            Assertions.AssertObject(Classify(Error("Main.cs"), line).Cause).IsEqual(BuildFailureCause.Unknown);
        // A failed build whose output has no readable error at all.
        Assertions.AssertObject(Classify("Build FAILED.", "compile error").Cause).IsEqual(BuildFailureCause.Unknown);
    }

    [TestCase]
    public static void GeneratedCodeIsUnknown()
    {
        Assertions.AssertObject(Classify(Error(".godot/mono/temp/obj/Debug/Generated.cs")).Cause).IsEqual(BuildFailureCause.Unknown);
        Assertions.AssertObject(Classify(Error("obj/Generated/Gen.g.cs")).Cause).IsEqual(BuildFailureCause.Unknown);
        // Source generator output that exists only in memory.
        var output = new[] { Error("Main.cs"), $"{At("Godot.SourceGenerators/Main_ScriptPath.generated.cs")}(1,1): error CS0101: duplicate" };
        Assertions.AssertObject(BuildDiagnostics.Classify(output, Root, fileExists: path => path.EndsWith("Main.cs", StringComparison.Ordinal)).Cause)
            .IsEqual(BuildFailureCause.Unknown);
    }

    [TestCase]
    public static void LockedFilesAndUnreachableSourcesAreTransientUnlessSomethingElseFailed()
    {
        var locked = $"C:\\Program Files\\dotnet\\sdk\\Microsoft.Common.CurrentVersion.targets(5034,5): error MSB3027: Could not copy \"Game.dll\". Exceeded retry count of 10. [{At("Game.csproj")}]";
        Assertions.AssertObject(Classify(locked).Cause).IsEqual(BuildFailureCause.Transient);
        Assertions.AssertObject(Classify(locked, Error("Main.cs")).Cause).IsEqual(BuildFailureCause.Transient);
        Assertions.AssertObject(Classify(locked, Error("addons/plug/Plug.cs")).Cause).IsEqual(BuildFailureCause.PluginCode);
    }

    [TestCase]
    public static void RepeatedErrorsOfTheSummaryAndCombinedOutputAreReadOnce()
    {
        // OS.Execute returns the whole output as one element.
        var output = string.Join("\r\n", Error("Main.cs"), "Build FAILED.", "", Error("Main.cs"), "    1 Error(s)");
        var diagnosis = Classify(output);
        Assertions.AssertInt(diagnosis.Errors.Count).IsEqual(1);
        Assertions.AssertObject(diagnosis.Cause).IsEqual(BuildFailureCause.ProjectCode);
    }

    [TestCase]
    public static void TheNodePrefixOfAParallelBuildLogIsNotPartOfThePath()
    {
        // As MSBuild's errors-only file log of a parallel build writes it, after a byte order mark.
        var diagnosis = Classify("﻿   1:7>" + Error("addons/plug/Plug.cs"), "2>" + Error("Main.cs"));
        Assertions.AssertInt(diagnosis.Errors.Count).IsEqual(2);
        Assertions.AssertObject(diagnosis.Cause).IsEqual(BuildFailureCause.PluginCode);
        Assertions.AssertString(diagnosis.Errors[0].File!).IsEqual(At("addons/plug/Plug.cs"));
    }

    [TestCase]
    public static void LocalizedMessagesKeepTheErrorKeyword()
    {
        // As a German SDK prints it.
        var diagnosis = Classify($"{At("Main.cs")}(2,83): error CS0117: \"Plug\" enthält keine Definition für \"Api\". [{At("Game.csproj")}]", "Fehler beim Buildvorgang.");
        Assertions.AssertObject(diagnosis.Cause).IsEqual(BuildFailureCause.ProjectCode);
        Assertions.AssertString(diagnosis.ProjectErrors.Single().Message).IsEqual("\"Plug\" enthält keine Definition für \"Api\".");
    }

    [TestCase]
    public static void WarningsAreNotErrors()
    {
        var diagnosis = Classify($"{At("addons/plug/Plug.cs")}(1,1): warning CS0618: obsolete [{At("Game.csproj")}]", Error("Main.cs"));
        Assertions.AssertObject(diagnosis.Cause).IsEqual(BuildFailureCause.ProjectCode);
    }

    [TestCase]
    public static void ProjectErrorsAreCapped()
    {
        var output = Enumerable.Range(1, BuildDiagnostics.MaxProjectErrors + 5).Select(i => $"{At("Main.cs")}({i},1): error CS0103: missing [{At("Game.csproj")}]").ToArray();
        var diagnosis = Classify(output);
        Assertions.AssertInt(diagnosis.Errors.Count).IsEqual(BuildDiagnostics.MaxProjectErrors + 5);
        Assertions.AssertInt(diagnosis.ProjectErrors.Count).IsEqual(BuildDiagnostics.MaxProjectErrors);
    }

    [TestCase]
    public static void FormatGivesOneLine()
    {
        Assertions.AssertString(BuildDiagnostics.Format(new("Main.cs", 2, 83, "CS0117", "missing"))).IsEqual("Main.cs(2,83): CS0117 missing");
        Assertions.AssertString(BuildDiagnostics.Format(new(null, 0, 0, "NU1101", "missing"))).IsEqual("NU1101 missing");
    }
}
#endif
