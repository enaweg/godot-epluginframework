#if TOOLS
using System;
using System.IO;
using Enaweg.Plugin.Internal.Dotnet;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class ProjectReferencesTests
{
    private string _root = null!;

    [BeforeTest]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "project-references-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [AfterTest]
    public void Cleanup() => Directory.Delete(_root, true);

    [TestCase]
    public void PackageReferenceIsFoundCaseInsensitivelyAndAMissingOneIsReported()
    {
        var project = Write("Game.csproj", """
            <Project Sdk="Godot.NET.Sdk/4.4.0">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="First;Second" />
              </ItemGroup>
            </Project>
            """);
        Assertions.AssertBool(ProjectReferences.ReferencesPackage(project, "newtonsoft.json") == true).IsTrue();
        Assertions.AssertBool(ProjectReferences.ReferencesPackage(project, "Second") == true).IsTrue();
        Assertions.AssertBool(ProjectReferences.ReferencesPackage(project, "Serilog") == false).IsTrue();
    }

    [TestCase]
    public void UnreadableOrPropertyBasedProjectsAreUnknown()
    {
        Assertions.AssertBool(ProjectReferences.ReferencesPackage(Path.Combine(_root, "Missing.csproj"), "Serilog") is null).IsTrue();
        var broken = Write("Broken.csproj", "<Project><ItemGroup>");
        Assertions.AssertBool(ProjectReferences.ReferencesPackage(broken, "Serilog") is null).IsTrue();
        var property = Write("Property.csproj", """<Project><ItemGroup><PackageReference Include="$(LoggingPackage)" /></ItemGroup></Project>""");
        Assertions.AssertBool(ProjectReferences.ReferencesPackage(property, "Serilog") is null).IsTrue();
    }

    [TestCase]
    public void ProjectReferenceIsResolvedRelativeToTheProject()
    {
        var project = Write("Game.csproj", """
            <Project>
              <ItemGroup>
                <ProjectReference Include="addons\tool\Tool.csproj" />
              </ItemGroup>
            </Project>
            """);
        Assertions.AssertBool(ProjectReferences.ReferencesProject(project, Path.Combine(_root, "addons/tool/Tool.csproj")) == true).IsTrue();
        Assertions.AssertBool(ProjectReferences.ReferencesProject(project, Path.Combine(_root, "addons/other/Other.csproj")) == false).IsTrue();
    }

    [TestCase]
    public void SolutionAndSlnxEntriesAreFound()
    {
        var solution = Write("Game.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Game", "Game.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Tool", "addons\tool\Tool.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
        Assertions.AssertBool(ProjectReferences.SolutionContains(solution, Path.Combine(_root, "addons/tool/Tool.csproj")) == true).IsTrue();
        Assertions.AssertBool(ProjectReferences.SolutionContains(solution, Path.Combine(_root, "addons/other/Other.csproj")) == false).IsTrue();
        var slnx = Write("Game.slnx", """
            <Solution>
              <Project Path="Game.csproj" />
              <Folder Name="/plugins/">
                <Project Path="addons/tool/Tool.csproj" />
              </Folder>
            </Solution>
            """);
        Assertions.AssertBool(ProjectReferences.SolutionContains(slnx, Path.Combine(_root, "addons/tool/Tool.csproj")) == true).IsTrue();
        Assertions.AssertBool(ProjectReferences.SolutionContains(slnx, Path.Combine(_root, "addons/other/Other.csproj")) == false).IsTrue();
    }

    [TestCase]
    public void InvalidCallsAreRecognized()
    {
        Assertions.AssertBool(ProjectReferences.IsValidPackageId("Newtonsoft.Json")).IsTrue();
        Assertions.AssertBool(ProjectReferences.IsValidPackageId("")).IsFalse();
        Assertions.AssertBool(ProjectReferences.IsValidPackageId("bad name")).IsFalse();
        Assertions.AssertBool(ProjectReferences.IsValidPackageId(new string('a', 101))).IsFalse();
        Assertions.AssertBool(ProjectReferences.IsValidProjectPath("addons/tool/Tool.csproj")).IsTrue();
        Assertions.AssertBool(ProjectReferences.IsValidProjectPath("addons/tool/readme.txt")).IsFalse();
        Assertions.AssertBool(ProjectReferences.IsValidProjectPath(" ")).IsFalse();
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }
}
#endif
