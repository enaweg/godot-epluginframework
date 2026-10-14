#if TOOLS
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class GodotConsoleLoggerTests
{
    [TestCase]
    public void ErrorPrinterBindsSoMessagesNameTheirCaller()
    {
        // Without it errors still print, but at GodotConsoleLogger instead of the code that logged them.
        Assertions.AssertBool(GodotConsoleLogger.NamesCallers).IsTrue();
    }
}
#endif
