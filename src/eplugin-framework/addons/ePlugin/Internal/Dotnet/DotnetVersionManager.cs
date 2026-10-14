#if TOOLS
using System;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal.Dotnet;

/// <summary>
/// Detects the installed dotnet CLI once per assembly load and hands out the <see cref="IDotnetCli"/> implementation
/// for its generation.
/// </summary>
[Tool]
internal sealed class DotnetVersionManager : ExecuteCliBase
{
    private const string CmdDotNet = "dotnet";
    private static readonly object ProbeLock = new();
    // Starting dotnet takes a while; Initialize, SwitchLogging and early recovery all ask for it.
    private static Probe? _cachedProbe;

    private readonly bool _enableDebugLogging;
    private readonly IProcessRunner? _runner;
    private readonly Probe _probe;

    /// <summary>What <c>dotnet --version</c> printed, or null when it could not be run.</summary>
    private sealed record Probe(string? Version, string Output);

    /// <summary>The version <c>dotnet --version</c> reports, or null when it cannot be run.</summary>
    public string? DotnetVersion => _probe.Version;

    public bool IsDotnetAvailable => _probe.Version is not null;

    public bool Is10OrLater => ParseMajorVersion(DotnetVersion) >= 10;

    /// <param name="runner">Runs dotnet; null for the editor's, whose answer is cached until the assembly reloads.</param>
    public DotnetVersionManager(ILogger? logger, bool enableDebugLogging, IProcessRunner? runner = null)
        : base(logger, enableDebugLogging, runner)
    {
        _enableDebugLogging = enableDebugLogging;
        _runner = runner;
        if (runner is null)
        {
            lock (ProbeLock)
            {
                _probe = _cachedProbe ??= ReadDotnetVersion();
            }
        }
        else
        {
            _probe = ReadDotnetVersion();
        }

        if (!IsDotnetAvailable)
        {
            logger?.Error(
                "The dotnet command line tool could not be executed, make sure it is installed and accessible." +
                (_probe.Output.Length > 0 ? $" It reported: {_probe.Output}" : ""));
        }
    }

    public IDotnetCli Create(ILogger? logger)
    {
        return Is10OrLater
            ? new DotnetCli10(logger, _enableDebugLogging, _runner)
            : new DotnetCli9(logger, _enableDebugLogging, _runner);
    }

    private Probe ReadDotnetVersion()
    {
        // A missing executable fails to start (exit code -1); a global.json naming a missing SDK exits with an error.
        var (exitCode, output) = ExecuteCall(CmdDotNet, ["--version"]);
        var text = string.Join("\n", output).Trim();
        var version = exitCode == 0 && output.Length > 0 ? output[0].Trim() : null;
        return new Probe(string.IsNullOrEmpty(version) ? null : version, text);
    }

    internal static int? ParseMajorVersion(string? version)
    {
        var majorIndex = version?.IndexOf('.') ?? -1;
        return majorIndex > 0 && int.TryParse(version!.AsSpan(..majorIndex), out var major) ? major : null;
    }
}
#endif
