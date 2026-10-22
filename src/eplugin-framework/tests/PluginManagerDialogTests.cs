#if TOOLS
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal.Manager;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

/// <summary>The ePlugin Manager on a fixture's framework, whose editor enables and disables the plugins it lists.</summary>
[TestSuite]
[RequireGodotRuntime]
public class PluginManagerDialogTests : IDisposable
{
    private LifecycleFixture _fixture = null!;
    private EPluginManagerDialog _dialog = null!;

    [BeforeTest]
    public void Setup()
    {
        _fixture = new LifecycleFixture();
        _fixture.AddPlainPlugin("ePlugin", "0.12.0");
        _fixture.AddPlainPlugin("tool", "2.1.0", "author=\"Someone\"\ndescription=\"Does things.\"");
        _fixture.AddPlainPlugin("other");
        _fixture.Host.Enabled.Add("tool");
        _fixture.Global.InitializeUpdateSites();
        _fixture.Global.InitializeLocalSources();
        _dialog = EPluginManagerDialog.Create(_fixture.Global, CancellationToken.None);
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(_dialog);
        _dialog.Refresh();
    }

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        if (_dialog is not null && GodotObject.IsInstanceValid(_dialog)) _dialog.Free();
        _fixture?.Dispose();
        GC.SuppressFinalize(this);
    }

    private Tree PluginTree => _dialog.GetNode<Tree>("%PluginTree");

    private TreeItem Row(string slug) => PluginTree.GetRoot().GetChildren().Single(i => i.GetMetadata(1).AsString() == slug);

    [TestCase]
    public void EveryPluginIsListedWithWhetherTheEditorHasItEnabled()
    {
        Assertions.AssertThat(PluginTree.GetRoot().GetChildren().Select(i => (i.GetMetadata(1).AsString(), i.IsChecked(0))).ToArray())
            .ContainsExactlyInAnyOrder(("ePlugin", true), ("tool", true), ("other", false));
        Assertions.AssertString(Row("tool").GetText(2)).IsEqual("2.1.0");

        Row("tool").Select(1);
        PluginTree.EmitSignal(Tree.SignalName.ItemSelected);

        Assertions.AssertString(_dialog.GetNode<Label>("%DetailsName").Text).Contains("tool");
        Assertions.AssertString(_dialog.GetNode<RichTextLabel>("%DetailsText").Text).StartsWith("Does things.");
    }

    [TestCase]
    public async Task DisablingTheFrameworkAfterConfirmingGoesThroughTheEditor()
    {
        _dialog.GetNode<ConfirmationDialog>("%DisableFrameworkConfirm").EmitSignal(AcceptDialog.SignalName.Confirmed);
        // not inside the confirmation's signal, which disabling the framework would free
        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("ePlugin")).IsTrue();
        Assertions.AssertBool(_dialog.Visible).IsFalse();

        var tree = (SceneTree)Engine.GetMainLoop();
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("ePlugin")).IsFalse();
        Assertions.AssertBool(_fixture.Host.IsPluginEnabled("tool")).IsTrue();
    }
}
#endif
