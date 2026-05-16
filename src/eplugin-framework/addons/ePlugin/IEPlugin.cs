using Enaweg.Plugin.Logging;

namespace Enaweg.Plugin;

public interface IEPlugin
{
    bool EnableDebugLogging { get; }
    
    ILogger Logger { get; }
}