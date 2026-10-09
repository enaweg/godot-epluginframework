#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Enaweg.Plugin.Internal.Update;

internal sealed record GitUrl(string Repository, string Path, string? Ref)
{
    public static bool TryParse(string text, out GitUrl? result)
    {
        result = null;
        if (text.Any(char.IsControl) || text.StartsWith('-')) return false;
        var hash = text.IndexOf('#');
        var reference = hash < 0 ? null : Uri.UnescapeDataString(text[(hash + 1)..]);
        if (hash >= 0) text = text[..hash];
        var query = text.IndexOf('?');
        var path = "";
        if (query >= 0)
        {
            var value = text[(query + 1)..];
            if (!value.StartsWith("path=", StringComparison.Ordinal) || value.Contains('&')) return false;
            path = Uri.UnescapeDataString(value[5..]);
            text = text[..query];
        }
        if (path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl) ||
            path.Split('/').Any(p => p is "." or "..") || path.Length > 0 && path.Split('/').Any(p => p.Length == 0) ||
            reference is not null && (reference.Length == 0 || reference.StartsWith('-') || reference.Any(char.IsWhiteSpace) || reference.Contains(".."))) return false;
        try { if (path.Length > 0) PackageFiles.Normalize(path); }
        catch (InvalidDataException) { return false; }
        var valid = Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme is "https" or "ssh") &&
            (uri.Scheme != "https" || uri.UserInfo.Length == 0);
        valid |= Regex.IsMatch(text, @"^git@[a-zA-Z0-9.-]+:[a-zA-Z0-9_./-]+\.git$");
        if (!valid) return false;
        result = new(text, path, reference);
        return true;
    }
}
internal sealed record GitOutcome(int ExitCode, string Output, string Error);
internal interface IGitRunner
{
    Task<GitOutcome> RunAsync(string directory, IReadOnlyList<string> arguments, CancellationToken ct, bool trace = false);
}
internal sealed class GitRunner(bool allowFileProtocol = false) : IGitRunner
{
    public async Task<GitOutcome> RunAsync(string directory, IReadOnlyList<string> arguments, CancellationToken ct, bool trace = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(arguments.Contains("checkout") ? 300 : 60));
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var value in new[] { "-c", "protocol.file.allow=" + (allowFileProtocol ? "always" : "never"), "-c", "protocol.ext.allow=never", "-c", "protocol.version=2" }.Concat(arguments)) start.ArgumentList.Add(value);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        if (trace) start.Environment["GIT_TRACE_PACKET"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Cannot start git; git >= 2.25 is required.");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
internal sealed class GitSource(UpdateHttp http, IGitRunner git, GitUrl url) : IUpdateSource, IVersionListSource
{
    /// <summary>
    /// Every semver tag is a version. The announced version is the tag name; the validator warns when the tagged
    /// plugin.cfg differs. A source pinned to a branch, tag or commit has no versions to choose from.
    /// </summary>
    public async Task<IReadOnlyList<UpdateCandidate>> ListAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (url.Ref is not null) return [];
        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eplugin-git-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await EnsureVersion(work, ct).ConfigureAwait(false);
            var remote = await Checked(git, work, ["ls-remote", "--tags", "--", url.Repository], ct).ConfigureAwait(false);
            var refs = remote.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Split('\t')).Where(p => p.Length == 2).ToArray();
            return refs.Where(p => p[1].StartsWith("refs/tags/", StringComparison.Ordinal) && !p[1].EndsWith("^{}", StringComparison.Ordinal) &&
                    SemVer.TryParse(p[1][10..], out var v) && (options.AllowPrerelease || v.Prerelease is null))
                .Select(p =>
                {
                    var tag = p[1][10..];
                    var commit = refs.FirstOrDefault(r => r[1] == p[1] + "^{}")?[0] ?? p[0];
                    SemVer.TryParse(tag, out var version);
                    return (Commit: commit, Candidate: new UpdateCandidate(target.Slug, target.Name, target.InstalledVersion, version.ToString(),
                        target.UpdateUrl!, null, commit, new GitPackageRef(url.Repository, url.Path, commit)));
                })
                .Where(c => Regex.IsMatch(c.Commit, "^[a-fA-F0-9]{40}$")).Select(c => c.Candidate).ToArray();
        }
        finally { Directory.Delete(work, true); }
    }
    private static async Task<GitOutcome> Checked(IGitRunner runner, string directory, string[] args, CancellationToken ct, bool trace = false)
    {
        var result = await runner.RunAsync(directory, args, ct, trace).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("git operation failed: " + result.Error);
        return result;
    }
    private async Task EnsureVersion(string work, CancellationToken ct)
    {
        var version = await Checked(git, work, ["--version"], ct).ConfigureAwait(false);
        var match = Regex.Match(version.Output, @"\d+\.\d+");
        if (!Version.TryParse(match.Value, out var parsed) || parsed < new Version(2, 25)) throw new IOException("git >= 2.25 required.");
    }
    public async Task<UpdateCandidate?> CheckAsync(PluginUpdateTarget target, UpdateCheckOptions options, CancellationToken ct)
    {
        if (url.Ref is not null && Regex.IsMatch(url.Ref, "^[a-fA-F0-9]{40}$")) return null;
        var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "eplugin-git-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await EnsureVersion(work, ct).ConfigureAwait(false);
            var remote = await Checked(git, work, ["ls-remote", "--heads", "--tags", "--", url.Repository], ct).ConfigureAwait(false);
            var refs = remote.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Split('\t')).Where(p => p.Length == 2).ToArray();
            string? commit;
            string? reference = url.Ref;
            if (reference is not null)
            {
                if (refs.Any(p => p[1] == "refs/tags/" + reference || p[1] == "refs/tags/" + reference + "^{}")) return null;
                commit = refs.FirstOrDefault(p => p[1] == "refs/heads/" + reference)?[0];
                if (commit is null) throw new IOException("Git ref is neither a branch nor a tag.");
            }
            else
            {
                reference = refs.Where(p => p[1].StartsWith("refs/tags/", StringComparison.Ordinal) && !p[1].EndsWith("^{}", StringComparison.Ordinal) &&
                    SemVer.TryParse(p[1][10..], out var v) && (options.AllowPrerelease || v.Prerelease is null))
                    .OrderByDescending(p => { SemVer.TryParse(p[1][10..], out var v); return v; }).FirstOrDefault()?[1][10..];
                commit = reference is null ? null : refs.FirstOrDefault(p => p[1] == "refs/tags/" + reference + "^{}")?[0] ?? refs.First(p => p[1] == "refs/tags/" + reference)[0];
                if (commit is null)
                {
                    var head = await Checked(git, work, ["ls-remote", "--", url.Repository, "HEAD"], ct).ConfigureAwait(false);
                    commit = head.Output.Split(new[] { '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                }
            }
            if (commit is null || !Regex.IsMatch(commit, "^[a-fA-F0-9]{40}$")) throw new IOException("Git source returned no usable commit.");
            string metadata;
            if (Uri.TryCreate(url.Repository, UriKind.Absolute, out var repo) && repo.Host == "github.com")
            {
                var path = repo.AbsolutePath.Trim('/');
                if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
                metadata = await http.ReadTextAsync($"https://raw.githubusercontent.com/{path}/{commit}/{(url.Path.Length == 0 ? "" : url.Path + "/")}plugin.cfg", ct,
                    Environment.GetEnvironmentVariable("EPLUGIN_GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")).ConfigureAwait(false);
            }
            else if (repo is not null && repo.Host == "gitlab.com")
            {
                var path = repo.AbsolutePath.Trim('/');
                if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
                metadata = await http.ReadTextAsync($"https://{repo.Authority}/{path}/-/raw/{commit}/{(url.Path.Length == 0 ? "" : url.Path + "/")}plugin.cfg", ct,
                    Environment.GetEnvironmentVariable("EPLUGIN_GITLAB_TOKEN") ?? Environment.GetEnvironmentVariable("GITLAB_TOKEN"), true).ConfigureAwait(false);
            }
            else
            {
                await Prepare(work, url.Repository, commit, ct).ConfigureAwait(false);
                metadata = (await Checked(git, work, ["show", $"FETCH_HEAD:{(url.Path.Length == 0 ? "" : url.Path + "/")}plugin.cfg"], ct).ConfigureAwait(false)).Output;
            }
            var version = PluginIni.Parse(metadata).GetValueOrDefault("version");
            if (!SemVer.TryParse(version, out var parsed)) throw new IOException("Remote plugin.cfg has no comparable version.");
            return new(target.Slug, target.Name, target.InstalledVersion, parsed.ToString(), target.UpdateUrl!, null, commit,
                new GitPackageRef(url.Repository, url.Path, commit));
        }
        finally { Directory.Delete(work, true); }
    }

    private async Task Prepare(string work, string repository, string commit, CancellationToken ct)
    {
        var capabilities = await Checked(git, work, ["ls-remote", "--", repository, "HEAD"], ct, true).ConfigureAwait(false);
        if (!Regex.IsMatch(capabilities.Error, @"(?:fetch=.*|capabilities.*)\bfilter\b")) throw new IOException("Git server does not advertise partial-fetch support; refusing a full repository download.");
        await Checked(git, work, ["init", "-q"], ct).ConfigureAwait(false);
        await Checked(git, work, ["remote", "add", "origin", repository], ct).ConfigureAwait(false);
        await Checked(git, work, ["config", "remote.origin.promisor", "true"], ct).ConfigureAwait(false);
        await Checked(git, work, ["config", "remote.origin.partialclonefilter", "blob:none"], ct).ConfigureAwait(false);
        var fetched = await Checked(git, work, ["fetch", "--depth", "1", "--filter=blob:none", "origin", commit], ct).ConfigureAwait(false);
        if (fetched.Error.Contains("filtering not recognized", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Git server ignored the partial-fetch filter.");
    }
    public async Task FetchAsync(GitPackageRef package, string destination, CancellationToken ct)
    {
        var work = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!, "git-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await EnsureVersion(work, ct).ConfigureAwait(false);
            await Prepare(work, package.Repository, package.Commit, ct).ConfigureAwait(false);
            await Checked(git, work, ["sparse-checkout", "init", "--no-cone"], ct).ConfigureAwait(false);
            await Checked(git, work, ["sparse-checkout", "set", "--no-cone", "--", package.Path.Length == 0 ? "/*" : "/" + package.Path + "/"], ct).ConfigureAwait(false);
            await Checked(git, work, ["checkout", "--detach", "FETCH_HEAD"], ct).ConfigureAwait(false);
            PackageFiles.Copy(System.IO.Path.Combine(work, package.Path), destination, ct);
        }
        finally { Directory.Delete(work, true); }
    }
}
#endif
