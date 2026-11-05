#if TOOLS
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// Disabling the framework drops <see cref="EGlobal.Instance"/>: Godot frees the plugin's instance and enabling the
/// framework again creates another, which must initialize instead of using the freed one.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EGlobalDetachTests
{
    [TestCase]
    public static void DisablingTheFrameworkLetsTheNextInstanceInitialize()
    {
        var framework = new EPluginPlugin();
        try
        {
            var global = EGlobal.Instance;
            global.Attach(framework, new GenericLoggerFactory(null), null);

            EGlobal.Detach(framework);

            Assertions.AssertBool(global.IsValid()).IsFalse();
            Assertions.AssertObject(EGlobal.Instance).IsNotSame(global);
            Assertions.AssertBool(EGlobal.Instance.IsValid()).IsFalse();
        }
        finally
        {
            EGlobal.Detach(framework);
            framework.Free();
        }
    }

    [TestCase]
    public static void AnotherInstanceKeepsTheFrameworkOfTheCurrentOne()
    {
        var framework = new EPluginPlugin();
        var other = new EPluginPlugin();
        try
        {
            var global = EGlobal.Instance;
            global.Attach(framework, new GenericLoggerFactory(null), null);

            EGlobal.Detach(other);

            Assertions.AssertObject(EGlobal.Instance).IsSame(global);
            Assertions.AssertBool(global.IsValid()).IsTrue();
        }
        finally
        {
            EGlobal.Detach(framework);
            framework.Free();
            other.Free();
        }
    }

    [TestCase]
    public static void WorkPostedForADetachedInstanceIsSkipped()
    {
        var framework = new EPluginPlugin();
        try
        {
            EGlobal.DiscardPosted();
            var global = EGlobal.Instance;
            global.Attach(framework, new GenericLoggerFactory(null), null);
            var ran = 0;
            global.PostForInstance(() => ran++);
            EGlobal.RunPosted();
            Assertions.AssertInt(ran).IsEqual(1);

            // e.g. failed plugins to disable: the framework is disabled before the next frame runs it
            global.PostForInstance(() => ran++);
            EGlobal.Detach(framework);
            EGlobal.RunPosted();

            Assertions.AssertInt(ran).IsEqual(1);
        }
        finally
        {
            EGlobal.Detach(framework);
            framework.Free();
        }
    }
}
#endif
