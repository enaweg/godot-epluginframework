#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Enaweg.Plugin.Internal.Update;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Local git repositories in a temporary folder: a bare "remote" with a tagged commit per plugin version, and a project
/// repository that has it as the submodule <c>addons/&lt;slug&gt;</c>, checked out at the first version.
/// </summary>
internal sealed class GitFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "eplugin-git-" + Guid.NewGuid().ToString("N"));
    public string Remote => Path.Combine(Root, "remote.git");
    public string Author => Path.Combine(Root, "author");
    public string Project => Path.Combine(Root, "project");
    public string Slug { get; }
    public string Submodule => Path.Combine(Project, "addons", Slug);
    /// <summary>The commit of every version's tag.</summary>
    public Dictionary<string, string> Commits { get; } = [];

    private GitFixture(string slug) { Slug = slug; }

    /// <summary>A remote with a commit tagged <c>v&lt;version&gt;</c> per version, the project at the first one.</summary>
    public static GitFixture Create(string slug = "plugin", params string[] versions)
    {
        if (versions.Length == 0) versions = ["1.0.0", "1.1.0", "2.0.0"];
        var fixture = new GitFixture(slug);
        Directory.CreateDirectory(fixture.Root);
        Git(fixture.Root, "init", "-q", "--bare", "remote.git");
        Directory.CreateDirectory(fixture.Author);
        Git(fixture.Author, "init", "-q", "-b", "main");
        foreach (var version in versions) fixture.Release(version);
        Git(fixture.Author, "remote", "add", "origin", fixture.Remote);
        Git(fixture.Author, "push", "-q", "origin", "main", "--tags");
        Directory.CreateDirectory(fixture.Project);
        Git(fixture.Project, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(fixture.Project, "project.godot"), "config_version=5\n");
        Git(fixture.Project, "add", ".");
        Git(fixture.Project, "commit", "-q", "-m", "project");
        Git(fixture.Project, "submodule", "add", "-q", fixture.Remote, "addons/" + slug);
        Git(fixture.Submodule, "checkout", "-q", "--detach", "v" + versions[0]);
        Git(fixture.Project, "add", ".");
        Git(fixture.Project, "commit", "-q", "-m", "add plugin");
        return fixture;
    }

    /// <summary>Commits <paramref name="version"/> in the author's repository and tags it; not pushed.</summary>
    public string Release(string version, string? extraFile = null)
    {
        File.WriteAllText(Path.Combine(Author, "plugin.cfg"), Config(version));
        File.WriteAllText(Path.Combine(Author, "plugin.gd"), "@tool\nextends EditorPlugin\n");
        if (extraFile is not null) File.WriteAllText(Path.Combine(Author, extraFile), version);
        Git(Author, "add", ".");
        Git(Author, "commit", "-q", "-m", "release " + version);
        Git(Author, "tag", "v" + version);
        return Commits[version] = Head(Author);
    }

    public static string Config(string version) => $"[plugin]\nname=\"Plugin\"\nversion=\"{version}\"\nscript=\"plugin.gd\"\n";

    public static string Head(string directory) => Git(directory, "rev-parse", "HEAD");

    /// <summary>The submodule as update checks see it.</summary>
    public PluginUpdateTarget Target(string installed = "1.0.0", Internal.Kinds.PluginKind kind = Internal.Kinds.PluginKind.Unknown) =>
        new(Slug, "Plugin", installed, null, Submodule, Kind: kind, Repository: PluginRepository.Detect(Submodule, Project));

    /// <summary>Runs git in <paramref name="directory"/>, as a user without signing or prompts; fails the test when git does.</summary>
    public static string Git(string directory, params string[] arguments)
    {
        var (code, output, error) = TryGit(directory, arguments);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed in {directory}: {error}");
        return output.Trim();
    }

    public static (int Code, string Output, string Error) TryGit(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var value in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.org", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false",
                     "-c", "protocol.file.allow=always", "-c", "core.autocrlf=false", "-c", "init.defaultBranch=main" })
            start.ArgumentList.Add(value);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result, error.Result);
    }

    public void Dispose() => AddonFiles.DeleteTree(Root);
}
#endif
