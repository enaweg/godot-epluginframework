#if TOOLS
namespace Enaweg.Plugin.Logging;

/// <summary>
/// A Logger that does not do anything, this logger is used as the default logger.
/// </summary>
public sealed class NullLogger : ILogger
{
    public void Log(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message)
    {
    }
}
#endif