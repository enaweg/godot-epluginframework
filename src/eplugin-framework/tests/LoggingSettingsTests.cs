using Enaweg.Plugin.Internal;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class LoggingSettingsTests
{
    [AfterTest]
    public void Cleanup() => ProjectSettings.SetSetting(LoggingSettings.DebugKey, false);

    [TestCase]
    public void DebugLoggingFollowsTheProjectSettingAndIsOffByDefault()
    {
        LoggingSettings.Register();
        var framework = new EPluginPlugin();
        try
        {
            Assertions.AssertBool(framework.EnableDebugLogging).IsFalse();
            ProjectSettings.SetSetting(LoggingSettings.DebugKey, true);
            Assertions.AssertBool(framework.EnableDebugLogging).IsTrue();
        }
        finally
        {
            framework.Free();
        }
    }
}
