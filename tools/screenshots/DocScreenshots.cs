#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Licenses;
using Enaweg.Plugin.Internal.Manager;
using Enaweg.Plugin.Internal.Update;
using Enaweg.Plugin.Internal.Welcomes;
using Godot;

namespace Enaweg.Plugin.DocScreenshots;

/// <summary>
/// Opens the framework's dialogs with sample data and has capture.cs photograph each window with its frame.
/// capture.cs copies this file into the project and registers it as an autoload for one editor run; it is not part of
/// the framework. Every dialog is opened the way the framework opens it, so the screenshots show the real layout.
/// </summary>
[Tool]
public partial class DocScreenshots : Node
{
    private const string OptionalSlug = "sample_optional_plugin";
    private string _out = "", _capture = "", _releases = "", _journal = "";
    private Node _editor = null!;

    public override void _Ready()
    {
        if (!Engine.IsEditorHint() || OS.GetEnvironment("EPLUGIN_SCREENSHOTS_OUT") is not { Length: > 0 } output) return;
        _out = output;
        _capture = OS.GetEnvironment("EPLUGIN_SCREENSHOTS_CAPTURE");
        _releases = OS.GetEnvironment("EPLUGIN_SCREENSHOTS_RELEASES");
        _journal = OS.GetEnvironment("EPLUGIN_SCREENSHOTS_JOURNAL");
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        var failed = false;
        try
        {
            _editor = EditorInterface.Singleton.GetBaseControl();
            await WaitUntil(() => EGlobal.Instance.IsValid() && !EGlobal.Instance.IsIndexingLocalSources, 120);
            // The startup update check runs in the background; let it finish so the status line is settled.
            await Seconds(Duration("EPLUGIN_SCREENSHOTS_SETTLE", 15));
            await Toolbar();
            await Manager();
            await Licenses();
            await Welcome();
            await UpdateFailure();
            await Progress();
        }
        catch (Exception ex)
        {
            failed = true;
            GD.PrintErr($"DocScreenshots failed: {ex}");
        }
        GD.Print("DocScreenshots done.");
        GetTree().Quit(failed ? 1 : 0);
    }

    private async Task Toolbar()
    {
        var button = (Control)_editor.FindChild("EPluginManagerButton", true, false);
        var rect = button.GetGlobalRect();
        var scale = EditorInterface.Singleton.GetEditorScale();
        // The button and a few of the toolbar controls to its left, so it can be found in the editor.
        var crop = new Rect2(rect.Position - new Vector2(420, 3) * scale, rect.Size + new Vector2(420 + 10, 6) * scale);
        var outline = rect.Grow(2 * scale);
        await Seconds(0.5);
        Capture("eplugin-toolbar-button", WindowHandle(DisplayServer.MainWindowId), "--crop", Rect(crop), "--highlight", Rect(outline));
    }

    private static string Rect(Rect2 rect) => $"{(int)rect.Position.X},{(int)rect.Position.Y},{(int)rect.Size.X},{(int)rect.Size.Y}";

    private async Task Manager()
    {
        var global = EGlobal.Instance;
        var button = (Button)_editor.FindChild("EPluginManagerButton", true, false);
        button.EmitSignal(BaseButton.SignalName.Pressed);
        var manager = await WaitFor(() => _editor.GetNodeOrNull<EPluginManagerDialog>(nameof(EPluginManagerDialog)) is { Visible: true } d ? d : null);
        // Wide enough for every plugin name and the details.
        Resize(manager, 1180, 640);
        Select(manager.GetNode<Tree>("%PluginTree"), "sample_plugin");
        await Shot("eplugin-manager", manager);

        // Example 1.1.0 releases of the sample plugins in a local plugin directory.
        global.AddLocalDirectory(_releases);
        await Seconds(0.5);
        await WaitUntil(() => !global.IsIndexingLocalSources, 60);
        manager.Refresh();
        Select(manager.GetNode<Tree>("%PluginTree"), OptionalSlug);
        await Shot("eplugin-updates", manager);

        manager.GetNode<Button>("%LocalSourcesButton").EmitSignal(BaseButton.SignalName.Pressed);
        var localSources = manager.GetNode<Window>("%LocalSourcesDialog");
        await Shot("eplugin-local-directories", localSources);
        localSources.Hide();

        global.SetUpdateSite("gdUnit4", "https://github.com/MikeSchulze/gdUnit4/releases");
        try
        {
            manager.GetNode<Button>("%UpdateSitesButton").EmitSignal(BaseButton.SignalName.Pressed);
            var sites = manager.GetNode<Window>("%UpdateSitesDialog");
            Resize(sites, 1000, 520);
            var siteTree = sites.GetNode<Tree>("%SiteTree");
            // The built-in list starts collapsed; open it to show the add-ons it knows, installed ones first.
            siteTree.GetRoot().GetChild(1).Collapsed = false;
            Select(siteTree, "gdUnit4", scroll: false);
            await Shot("eplugin-update-sites", sites);

            sites.GetNode<Button>("%EditSiteButton").EmitSignal(BaseButton.SignalName.Pressed);
            var editor = sites.GetNode<Window>("%SiteEditor");
            await Shot("eplugin-update-site-editor", editor);
            editor.Hide();
            sites.Hide();
        }
        finally
        {
            global.RemoveUpdateSite("gdUnit4");
            global.RemoveLocalDirectory(_releases);
        }

        manager.GetNode<Button>("%AboutButton").EmitSignal(BaseButton.SignalName.Pressed);
        var about = manager.GetNode<Window>("%AboutDialog");
        await Shot("eplugin-about", about);
        about.Hide();
        manager.Hide();
        await Seconds(0.5);
    }

    private async Task Licenses()
    {
        var license = File.ReadAllText(ProjectSettings.GlobalizePath("res://addons/ePlugin/LICENSE"));
        var review = new LicenseReview(
        [
            new LicenseEntry("sample_plugin", "SamplePlugin", "1.0", "LICENSE", license),
            new LicenseEntry("sample_dependant_plugin", "Sample Dependant Plugin", "1.0", "LICENSE", license)
        ],
        [new LicenseActivation("sample_dependant_plugin", "Sample Dependant Plugin", ["sample_plugin", "sample_dependant_plugin"])]);
        // Halfway through: the dependency's license is accepted, the plugin's own is shown next.
        review.Accept("sample_plugin");
        var dialog = LicenseDialog.Create(review, _ => { });
        dialog.Open();
        await Shot("plugin-license", dialog);
        EditorWindows.Release(dialog);
        await Seconds(0.5);
    }

    private async Task Welcome()
    {
        WelcomeEntry Read(string slug, string name) =>
            PluginWelcome.FromConfig(slug, name, "1.0", null, ProjectSettings.GlobalizePath($"res://addons/{slug}"))
            ?? throw new InvalidOperationException($"{slug} has no welcome page.");
        var dialog = WelcomeDialog.Create([Read("sample_dependant_plugin", "Sample Dependant Plugin"), Read("sample_plugin", "SamplePlugin")]);
        dialog.Open();
        await Shot("plugin-welcome", dialog);
        EditorWindows.Release(dialog);
        await Seconds(0.5);
    }

    private async Task UpdateFailure()
    {
        var journal = new UpdateJournal
        {
            Id = Path.GetFileName(_journal),
            Directory = _journal,
            State = UpdatePhase.AwaitingDecision,
            Plugins = [new UpdatePluginJournal { Slug = "sample_plugin", IsEPlugin = true, WasEnabled = true, OldVersion = "1.0", NewVersion = "1.1.0" }],
            Builds = [new JournalBuild("final", 1, "final-build.log")]
        };
        var dialog = UpdateFailureDialog.Create(journal, _ => { });
        _editor.AddChild(dialog);
        dialog.Open();
        await Shot("update-build-failure", dialog);
        EditorWindows.Release(dialog);
        await Seconds(0.5);
    }

    private async Task Progress()
    {
        using (ActivationProgress.Begin("Enabling Sample Dependant Plugin..."))
        {
            ActivationProgress.SetText("Activating Sample Dependant Plugin...");
            // The helper is a separate process that shows its window after a short delay, then plays its opening
            // (about 3 s) before the label and progress bar are fully visible.
            await Seconds(6);
            Capture("activation-progress", "", "--title", "ePlugin Framework");
        }
    }

    private static void Resize(Window window, int width, int height)
    {
        window.Size = (Vector2I)(new Vector2(width, height) * EditorInterface.Singleton.GetEditorScale());
        window.MoveToCenter();
    }

    private static void Select(Tree tree, string key, bool scroll = true)
    {
        for (var item = tree.GetRoot()?.GetFirstChild(); item is not null; item = item.GetNextInTree())
        {
            for (var column = 0; column < tree.Columns; column++)
            {
                if (item.GetMetadata(column).AsString() != key) continue;
                item.Select(column);
                if (scroll) tree.ScrollToItem(item);
                return;
            }
        }
        throw new InvalidOperationException($"No row {key} in {tree.Name}.");
    }

    private async Task Shot(string name, Window window)
    {
        await WaitUntil(() => window.Visible, 10);
        // A few frames for layout, then enough time for the compositor to show the window.
        await Seconds(1.5);
        Capture(name, WindowHandle(window.GetWindowId()));
    }

    private void Capture(string name, string window, params string[] extra)
    {
        // EPLUGIN_SCREENSHOTS_CAPTURE is the running capture.cs program, which captures one window with `window`.
        var args = new List<string> { "window", "--out", Path.Combine(_out, name + ".png") };
        if (window.Length > 0) args.AddRange(["--window", window]);
        args.AddRange(extra);
        var output = new Godot.Collections.Array();
        var exit = OS.Execute(_capture, args.ToArray(), output, true);
        GD.Print($"DocScreenshots {name}: {string.Join("", output.Select(o => o.AsString())).Trim()}");
        if (exit != 0) throw new InvalidOperationException($"Capturing {name} failed with exit code {exit}.");
    }

    private static string WindowHandle(long windowId) =>
        DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle, (int)windowId).ToString();

    private static double Duration(string variable, double fallback) =>
        double.TryParse(OS.GetEnvironment(variable), out var value) ? value : fallback;

    private async Task Seconds(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task WaitUntil(Func<bool> condition, double timeout)
    {
        var start = Time.GetTicksMsec();
        while (!condition())
        {
            if (Time.GetTicksMsec() - start > timeout * 1000) throw new TimeoutException("Timed out waiting for the editor.");
            await Seconds(0.25);
        }
    }

    private async Task<T> WaitFor<T>(Func<T?> find) where T : class
    {
        T? found = null;
        await WaitUntil(() => (found = find()) is not null, 10);
        return found!;
    }
}
#endif
