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

    [TestCase]
    public static void AScaledSceneScalesItsMinimumSizesAndTheSpacingItOverrides()
    {
        var window = new Window { Visible = false };
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(100, 0) };
        box.AddThemeConstantOverride("separation", 8);
        var text = new RichTextLabel();
        text.AddThemeConstantOverride("line_separation", 6);
        // prepared on its own
        var confirm = new ConfirmationDialog();
        var inner = new VBoxContainer();
        inner.AddThemeConstantOverride("separation", 4);
        confirm.AddChild(inner);
        box.AddChild(text);
        window.AddChild(box);
        window.AddChild(confirm);
        try
        {
            EditorWindows.ScaleMinimumSizes(window, 2f);

            Assertions.AssertFloat(box.CustomMinimumSize.X).IsEqual(200f);
            Assertions.AssertInt(box.GetThemeConstant("separation")).IsEqual(16);
            Assertions.AssertInt(text.GetThemeConstant("line_separation")).IsEqual(12);
            // a constant the scene leaves to the theme stays the theme's
            Assertions.AssertBool(text.HasThemeConstantOverride("separation")).IsFalse();
            Assertions.AssertInt(inner.GetThemeConstant("separation")).IsEqual(4);
        }
        finally { window.Free(); }
    }

    [TestCase]
    public static void AChildWindowOpensNoLargerThanItsParent()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var window = new AcceptDialog { Visible = false, Size = new Vector2I(100000, 100000) };
        tree.Root.AddChild(window);
        try
        {
            EditorWindows.PopupChild(window);

            Assertions.AssertBool(window.Visible).IsTrue();
            Assertions.AssertInt(window.Size.X).IsLess(100000);
            Assertions.AssertInt(window.Size.Y).IsLess(100000);
        }
        finally { window.Free(); }
    }
}
#endif
