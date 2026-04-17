namespace Enaweg.Plugin.Logging;

public interface ILoggerFactory
{
    ILogger CreateLogger(string category);
}