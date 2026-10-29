#if TOOLS
using System;
using System.IO;
using System.Text;
using Enaweg.Plugin.Internal.Dotnet;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class ReleaseConfigurationTests
{
    private string _root = null!;

    [BeforeTest]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "release-configuration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [AfterTest]
    public void Cleanup() => Directory.Delete(_root, true);

    private string Library => Path.Combine(_root, "addons", "tool", ".support", "Support.csproj");

    [TestCase]
    public static void TheProbeAsksForOptimizeInExportRelease()
    {
        Assertions.AssertThat(ReleaseConfiguration.ProbeArguments("/p/Lib.csproj"))
            .ContainsExactly("/p/Lib.csproj", "-getProperty:Optimize", "-p:Configuration=ExportRelease");
        Assertions.AssertBool(ReleaseConfiguration.Optimizes(["true"])).IsTrue();
        // the value is the last line, after anything a first run of dotnet prints
        Assertions.AssertBool(ReleaseConfiguration.Optimizes(["Welcome to .NET!", " True ", ""])).IsTrue();
        Assertions.AssertBool(ReleaseConfiguration.Optimizes([""])).IsFalse();
        Assertions.AssertBool(ReleaseConfiguration.Optimizes(["false"])).IsFalse();
        Assertions.AssertBool(ReleaseConfiguration.Optimizes([])).IsFalse();
    }

    [TestCase]
    public void TheReferenceBuildsReleaseInExportReleaseAndKeepsTheFormatting()
    {
        // as dotnet add reference writes it, with Windows line breaks and a byte order mark
        var project = Write("Game.csproj", """
            <Project Sdk="Godot.NET.Sdk/4.4.1">
              <ItemGroup>
                <ProjectReference Include="addons\tool\.support\Support.csproj" />
                <ProjectReference Include="Other\Other.csproj" />
              </ItemGroup>
            </Project>

            """.Replace("\n", "\r\n"), byteOrderMark: true);

        Assertions.AssertBool(ReleaseConfiguration.MapReference(project, Library)).IsTrue();
        Assertions.AssertString(Read(project)).IsEqual("""
            <Project Sdk="Godot.NET.Sdk/4.4.1">
              <ItemGroup>
                <ProjectReference Include="addons\tool\.support\Support.csproj">
                  <SetConfiguration Condition=" '$(Configuration)' == 'ExportRelease' ">Configuration=Release</SetConfiguration>
                </ProjectReference>
                <ProjectReference Include="Other\Other.csproj" />
              </ItemGroup>
            </Project>

            """.Replace("\n", "\r\n"));
        Assertions.AssertBool(File.ReadAllBytes(project).AsSpan().StartsWith(Encoding.UTF8.Preamble)).IsTrue();

        // a retry adds nothing
        var mapped = File.ReadAllText(project);
        Assertions.AssertBool(ReleaseConfiguration.MapReference(project, Library)).IsTrue();
        Assertions.AssertString(File.ReadAllText(project)).IsEqual(mapped);
    }

    [TestCase]
    public void AConfigurationTheReferenceChoosesIsKept()
    {
        const string text = """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <ProjectReference Include="addons/tool/.support/Support.csproj" SetConfiguration="Configuration=Debug" />
              </ItemGroup>
            </Project>
            """;
        var project = Write("Game.csproj", text);
        Assertions.AssertBool(ReleaseConfiguration.MapReference(project, Library)).IsTrue();
        Assertions.AssertString(Read(project)).IsEqual(text);
    }

    [TestCase]
    public void AMissingReferenceOrProjectCannotBeMapped()
    {
        var project = Write("Game.csproj", """<Project><ItemGroup><ProjectReference Include="Other\Other.csproj" /></ItemGroup></Project>""");
        Assertions.AssertBool(ReleaseConfiguration.MapReference(project, Library)).IsFalse();
        Assertions.AssertBool(ReleaseConfiguration.MapReference(Path.Combine(_root, "Missing.csproj"), Library)).IsFalse();
        Assertions.AssertBool(ReleaseConfiguration.MapReference(Write("Broken.csproj", "<Project><ItemGroup>"), Library)).IsFalse();
    }

    [TestCase]
    public void TheSolutionMapsExportReleaseToTheProjectsRelease()
    {
        // as dotnet sln add writes it into a solution GodotTools created
        const string head = """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Game", "Game.csproj", "{11111111-2222-3333-4444-555555555555}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Support", "addons\tool\.support\Support.csproj", "{3800F341-EA5B-4D1F-A0D1-1EEC360F8DFF}"
            EndProject
            Global
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            		{11111111-2222-3333-4444-555555555555}.ExportRelease|Any CPU.ActiveCfg = ExportRelease|Any CPU
            		{3800F341-EA5B-4D1F-A0D1-1EEC360F8DFF}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{3800F341-EA5B-4D1F-A0D1-1EEC360F8DFF}.ExportDebug|Any CPU.ActiveCfg = Debug|Any CPU

            """;
        var solution = Write("Game.sln", (head + """
            		{3800F341-EA5B-4D1F-A0D1-1EEC360F8DFF}.ExportRelease|Any CPU.ActiveCfg = Debug|Any CPU
            		{3800f341-ea5b-4d1f-a0d1-1eec360f8dff}.ExportRelease|x64.Build.0 = Debug|x64
            	EndGlobalSection
            EndGlobal

            """).Replace("\n", "\r\n"), byteOrderMark: true);

        Assertions.AssertBool(ReleaseConfiguration.MapSolution(solution, Library)).IsTrue();
        // only the library's ExportRelease changes, not the game's or its other configurations
        Assertions.AssertString(Read(solution)).IsEqual((head + """
            		{3800F341-EA5B-4D1F-A0D1-1EEC360F8DFF}.ExportRelease|Any CPU.ActiveCfg = Release|Any CPU
            		{3800f341-ea5b-4d1f-a0d1-1eec360f8dff}.ExportRelease|x64.Build.0 = Release|x64
            	EndGlobalSection
            EndGlobal

            """).Replace("\n", "\r\n"));
        Assertions.AssertBool(File.ReadAllBytes(solution).AsSpan().StartsWith(Encoding.UTF8.Preamble)).IsTrue();
        Assertions.AssertBool(ReleaseConfiguration.MapSolution(solution, Path.Combine(_root, "Missing", "Missing.csproj"))).IsFalse();
        Assertions.AssertBool(ReleaseConfiguration.MapSolution(Path.Combine(_root, "Missing.sln"), Library)).IsFalse();
    }

    [TestCase]
    public void TheXmlSolutionGetsARuleForExportRelease()
    {
        var solution = Write("Game.slnx", """
            <Solution>
              <Folder Name="/addons/">
                <Project Path="addons/tool/.support/Support.csproj">
                  <BuildType Project="Debug" />
                </Project>
              </Folder>
              <Project Path="Game.csproj" />
            </Solution>
            """);
        Assertions.AssertBool(ReleaseConfiguration.MapSolution(solution, Library)).IsTrue();
        // it wins over the rule for every configuration, wherever it is
        Assertions.AssertString(Read(solution)).IsEqual("""
            <Solution>
              <Folder Name="/addons/">
                <Project Path="addons/tool/.support/Support.csproj">
                  <BuildType Project="Debug" />
                  <BuildType Solution="ExportRelease|*" Project="Release" />
                </Project>
              </Folder>
              <Project Path="Game.csproj" />
            </Solution>
            """);

        // a project without rules gets one, and an existing rule for ExportRelease is changed instead of added to
        var other = Write("Other.slnx", """
            <Solution>
              <Project Path="addons\tool\.support\Support.csproj" />
              <Project Path="Lib/Lib.csproj">
                <BuildType Solution="ExportRelease|Any CPU" Project="Debug" />
              </Project>
            </Solution>
            """);
        Assertions.AssertBool(ReleaseConfiguration.MapSolution(other, Library)).IsTrue();
        Assertions.AssertBool(ReleaseConfiguration.MapSolution(other, Path.Combine(_root, "Lib", "Lib.csproj"))).IsTrue();
        Assertions.AssertString(Read(other)).IsEqual("""
            <Solution>
              <Project Path="addons\tool\.support\Support.csproj">
                <BuildType Solution="ExportRelease|*" Project="Release" />
              </Project>
              <Project Path="Lib/Lib.csproj">
                <BuildType Solution="ExportRelease|Any CPU" Project="Release" />
              </Project>
            </Solution>
            """);
    }

    private string Write(string name, string content, bool byteOrderMark = false)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content, new UTF8Encoding(byteOrderMark));
        return path;
    }

    private static string Read(string path) => new UTF8Encoding(false).GetString(File.ReadAllBytes(path)).TrimStart('﻿');
}
#endif
