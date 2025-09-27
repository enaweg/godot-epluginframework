#if TOOLS
using System.Diagnostics;
using Godot;

namespace Enaweg.Plugin.Logging;

/// <summary>
/// This logger logs to the default Godot Console. This can be used to debug problems.
/// </summary>
/// <param name="prefix"></param>
public sealed class GodotConsoleLogger(string prefix) : ILogger
{
    [StackTraceHidden]
    public void Log(string message)
    {
        GD.Print($"{prefix}| {message}");
    }

    [StackTraceHidden]
    public void Warn(string message)
    {
        GD.PushWarning($"{prefix}| {message}");
    }

    [StackTraceHidden]
    public void Error(string message)
    {
        GD.PushError($"{prefix}| {message}");
    }
}
#endif