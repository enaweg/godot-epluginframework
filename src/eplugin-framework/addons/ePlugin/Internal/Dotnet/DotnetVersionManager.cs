#if TOOLS
using System;
using Enaweg.Plugin.Logging;
using Godot;

namespace Enaweg.Plugin.Internal.Dotnet;

[Tool]
internal sealed class DotnetVersionManager : ExecuteCliBase
{
    private readonly bool _enableDebugLogging;
    private const string CmdDotNet = "dotnet";

    private string? _dotnetVersion = null;
    private bool? _isDotnetAvailable = null;
    private bool? _isDotnet10OrLater = null;

    public string? DotnetVersion
    {
        get
        {
            if (_dotnetVersion is null)
            {
                _dotnetVersion = ReadDotnetVersion();
            }

            return _dotnetVersion;
        }
    }

    public bool IsDotnetAvailable
    {
        get
        {
            if (_isDotnetAvailable is null)
            {
                _isDotnetAvailable = IsDotnetCliAvailable();
            }

            if (_isDotnetAvailable.HasValue)
            {
                return _isDotnetAvailable.Value;
            }

            return false;
        }
    }

    public bool Is10OrLater
    {
        get
        {
            if (_isDotnet10OrLater is null)
            {
                var major = ParseMajorVersion(DotnetVersion);
                if (major is not null)
                {
                    _isDotnet10OrLater = major >= 10;
                }
                else
                {
                    _isDotnet10OrLater = false;
                }
            }

            return _isDotnet10OrLater.Value;
        }
    }

    public DotnetVersionManager(ILogger? logger, bool enableDebugLogging) : base(logger, enableDebugLogging)
    {
        _enableDebugLogging = enableDebugLogging;
        if (!IsDotnetAvailable)
        {
            logger?.Error(
                "The dotnet command line tool could not be executed, make sure it is installed and accessible.");
        }
    }

    public IDotnetCli Create(ILogger? logger)
    {
        if (Is10OrLater)
        {
            return new DotnetCli10(logger, _enableDebugLogging);
        }
        else
        {
            return new DotnetCli9(logger, _enableDebugLogging);
        }
    }

    private bool IsDotnetCliAvailable()
    {
        try
        {
            var cmdName = "which"; // works on linux and mac
            if (OS.GetName() == "Windows")
            {
                cmdName = "where";
            }

            var result = ExecuteCall(cmdName, [CmdDotNet]);
            if (result.Item1 >= 0)
            {
                return true;
            }
        }
        catch (Exception _)
        {
            // ignored
        }

        return false;
    }

    private string ReadDotnetVersion()
    {
        var result = ExecuteCall(CmdDotNet, ["--version"]);

        if (result.Item1 >= 0 && result.Item2.Length >= 1)
        {
            return result.Item2[0];
        }

        throw new Exception("Dotnet could not be executed.");
    }

    private int? ParseMajorVersion(string? version)
    {
        if (version is null)
        {
            return null;
        }

        var majorIndex = version.IndexOf('.');
        if (majorIndex < 0)
        {
            return null;
        }

        var majorString = version.AsSpan(..majorIndex);
        if (int.TryParse(majorString, out var major))
        {
            return major;
        }

        return null;
    }
}
#endif