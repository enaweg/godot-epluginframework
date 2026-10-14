using System;
using System.Diagnostics;
using System.Reflection;
using Godot;
using Godot.NativeInterop;

namespace Enaweg.Plugin.Logging;

/// <summary>
/// This logger logs to the default Godot Console. This can be used to debug problems.
/// </summary>
/// <remarks>
/// Errors and warnings name the code that logged them rather than this logger: <c>GD.PushError</c> always reports its
/// direct caller, so the location is passed to Godot's error printer explicitly.
/// </remarks>
/// <param name="prefix"></param>
public sealed class GodotConsoleLogger(string prefix) : ILogger
{
    private delegate void ErrPrintError(in godot_string function, in godot_string file, int line, in godot_string error,
        in godot_string message, godot_bool editorNotify, godot_error_handler_type type);

    // The printer GD.PushError uses, which takes the location as arguments. It is internal to GodotSharp, so without it
    // (a changed signature in another Godot version) messages fall back to GD.PushError.
    private static readonly ErrPrintError? Printer = Bind();

    /// <summary>Whether errors and warnings name the code that logged them; without it they name this logger.</summary>
    internal static bool NamesCallers => Printer is not null;

    [StackTraceHidden]
    public void Log(string message)
    {
        GD.Print($"{prefix} | {message}");
    }

    [StackTraceHidden]
    public void Warn(string message)
    {
        if (!Print($"{prefix} | {message}", godot_error_handler_type.ERR_HANDLER_WARNING)) GD.PushWarning($"{prefix} | {message}");
    }

    [StackTraceHidden]
    public void Error(string message)
    {
        if (!Print($"{prefix} | {message}", godot_error_handler_type.ERR_HANDLER_ERROR)) GD.PushError($"{prefix} | {message}");
    }

    private static ErrPrintError? Bind()
    {
        try
        {
            return typeof(NativeFuncs).GetMethod("godotsharp_err_print_error", BindingFlags.NonPublic | BindingFlags.Static)
                ?.CreateDelegate<ErrPrintError>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Print(string message, godot_error_handler_type type)
    {
        if (Printer is null || Caller() is not { } frame || frame.GetMethod() is not { } method) return false;
        try
        {
            var path = frame.GetFileName();
            using var function = Marshaling.ConvertStringToNative($"{method.DeclaringType?.FullName}.{method.Name}");
            using var file = Marshaling.ConvertStringToNative(string.IsNullOrEmpty(path) ? "" : ProjectSettings.LocalizePath(path));
            using var error = Marshaling.ConvertStringToNative(message);
            using var description = Marshaling.ConvertStringToNative("");
            Printer(function, file, frame.GetFileLineNumber(), error, description, godot_bool.False, type);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The first frame outside logging: loggers wrapping this one (any <see cref="ILogger"/>) and methods marked
    /// <see cref="StackTraceHiddenAttribute"/> are skipped.
    /// </summary>
    private static StackFrame? Caller()
    {
        foreach (var frame in new StackTrace(1, true).GetFrames())
        {
            var method = frame.GetMethod();
            if (method is null || method.IsDefined(typeof(StackTraceHiddenAttribute), false)) continue;
            if (method.DeclaringType is { } type && typeof(ILogger).IsAssignableFrom(type)) continue;
            return frame;
        }

        return null;
    }
}
