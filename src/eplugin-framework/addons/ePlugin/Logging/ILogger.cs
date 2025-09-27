#if TOOLS
namespace Enaweg.Plugin.Logging;

/// <summary>
/// Logger interface used by the internal logging of ePlugin.
/// </summary>
public interface ILogger
{
    /// <summary>
    /// Loggs an informational message.
    /// </summary>
    /// <param name="message"></param>
    void Log(string message);
    
    /// <summary>
    /// Loggs a warning.
    /// </summary>
    /// <param name="message"></param>
    void Warn(string message);
    
    /// <summary>
    /// Loggs a error.
    /// </summary>
    /// <param name="message"></param>
    void Error(string message);
}
#endif