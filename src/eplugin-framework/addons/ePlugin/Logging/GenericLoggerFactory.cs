using System;

namespace Enaweg.Plugin.Logging;

/// <summary>
/// An <see cref="ILoggerFactory"/> that creates its loggers with a function.
/// </summary>
/// <param name="createLogger">
/// Creates the logger of a category; null creates a <see cref="NullLogger"/> for every category.
/// </param>
public sealed class GenericLoggerFactory(Func<string, ILogger>? createLogger) : ILoggerFactory
{
    /// <inheritdoc/>
    public ILogger CreateLogger(string category)
    {
        if (createLogger is null)
        {
            return new NullLogger();
        }

        return createLogger(category);
    }
}