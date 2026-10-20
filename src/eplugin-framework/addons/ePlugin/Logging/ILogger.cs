using System.Diagnostics.CodeAnalysis;

namespace Enaweg.Plugin.Logging;

/// <summary>
/// Logger interface used by the internal logging of ePlugin.
/// </summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "Error is public API that logging plugins implement; renaming it would break them.")]
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
