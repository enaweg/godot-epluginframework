#if TOOLS
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Manager;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class EditorWindowsTests
{
    [TestCase]
    public static async Task ReleaseHidesAWindowNowAndFreesItOnlyLater()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var window = new Window { Visible = false };
        // A framework control with this name is not the engine's internal ProgressDialog class.
        var child = new Control { Name = "ProgressDialog" };
        window.AddChild(child);
        tree.Root.AddChild(window);

        EditorWindows.Release(window);
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        // Still alive frames after the hide, so the window messages that follow it find it.
        Assertions.AssertBool(GodotObject.IsInstanceValid(window)).IsTrue();
        Assertions.AssertBool(window.Visible).IsFalse();

        await tree.ToSignal(tree.CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        Assertions.AssertBool(GodotObject.IsInstanceValid(window)).IsFalse();
        Assertions.AssertBool(GodotObject.IsInstanceValid(child)).IsFalse();
    }

    [TestCase]
    public static void ReleasingAWindowOutsideTheTreeFreesItRightAway()
    {
        var window = new Window { Visible = false };
        EditorWindows.Release(window);
        Assertions.AssertBool(window.IsQueuedForDeletion()).IsTrue();
    }
}
#endif
