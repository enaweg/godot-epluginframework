using System;

namespace Enaweg.Plugin.Logging;

public sealed class GenericLoggerFactory(Func<string, ILogger>? createLogger) : ILoggerFactory
{
    public ILogger CreateLogger(string category)
    {
        if (createLogger is null)
        {
            return new NullLogger();
        }

        return createLogger(category);
    }
}