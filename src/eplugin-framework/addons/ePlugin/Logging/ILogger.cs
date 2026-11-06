using System.Diagnostics.CodeAnalysis;

namespace Enaweg.Plugin.Logging;

/// <summary>
/// Logger interface used by the internal logging of ePlugin. A logging plugin can implement it and take over ePlugin's
/// logging through an <see cref="ILoggerFactory"/>.
/// </summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "Error is public API that logging plugins implement; renaming it would break them.")]
public interface ILogger
{
    /// <summary>
    /// Logs an informational message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    void Log(string message);

    /// <summary>
    /// Logs a warning.
    /// </summary>
    /// <param name="message">The warning to log.</param>
    void Warn(string message);

    /// <summary>
    /// Logs an error.
    /// </summary>
    /// <param name="message">The error to log.</param>
    void Error(string message);
}
