using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin;

/// <summary>The running ePlugin framework, as <see cref="IInitialize.Initialize"/> and <c>EPlugin.Instance</c> pass it.</summary>
public interface IEPlugin
{
    /// <summary>
    /// Whether ePlugin logs in detail, e.g. every dotnet command it runs. Set with the <c>eplugin/logging/debug</c>
    /// project setting.
    /// </summary>
    bool EnableDebugLogging { get; }

    /// <summary>ePlugin's own log, which a logging plugin can take over.</summary>
    ILogger Logger { get; }
}
