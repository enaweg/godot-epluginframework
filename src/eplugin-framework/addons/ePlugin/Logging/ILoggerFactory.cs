namespace Enaweg.Plugin.Logging;

/// <summary>
/// Creates the loggers ePlugin uses: one for the framework and one for each plugin it manages.
/// </summary>
public interface ILoggerFactory
{
    /// <summary>
    /// Creates a logger for <paramref name="category"/>.
    /// </summary>
    /// <param name="category">What logs through it: the full type name of the framework or of a plugin.</param>
    /// <returns>The logger for <paramref name="category"/>.</returns>
    ILogger CreateLogger(string category);
}