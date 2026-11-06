
namespace Enaweg.Plugin.Logging;

/// <summary>
/// A logger that discards every message. <see cref="GenericLoggerFactory"/> creates it when it has no function.
/// </summary>
public sealed class NullLogger : ILogger
{
    /// <inheritdoc/>
    public void Log(string message)
    {
    }

    /// <inheritdoc/>
    public void Warn(string message)
    {
    }

    /// <inheritdoc/>
    public void Error(string message)
    {
    }
}
