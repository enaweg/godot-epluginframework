#if TOOLS
using System.Diagnostics;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class GodotConsoleLoggerTests
{
    [TestCase]
    public static void ErrorPrinterBindsSoMessagesNameTheirCaller()
    {
        // Without it errors still print, but at GodotConsoleLogger instead of the code that logged them.
        Assertions.AssertBool(GodotConsoleLogger.NamesCallers).IsTrue();
    }

    [TestCase]
    public static void TheLocationIsTheFirstFrameOutsideLogging()
    {
        // a logger wrapping GodotConsoleLogger and a helper hidden from stack traces are skipped
        var frame = WrappingLogger.Locate();

        Assertions.AssertString(frame!.GetMethod()!.Name).IsEqual(nameof(TheLocationIsTheFirstFrameOutsideLogging));
        Assertions.AssertString(frame.GetFileName()).EndsWith("GodotConsoleLoggerTests.cs");
    }

    private sealed class WrappingLogger : ILogger
    {
        public static StackFrame? Locate() => Hidden();

        [StackTraceHidden]
        private static StackFrame? Hidden() => GodotConsoleLogger.Caller();

        public void Log(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }
}
#endif
