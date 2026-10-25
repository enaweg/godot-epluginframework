#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Logging;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class ShowHideHelperTests : IDisposable
{
    private string _root = null!;
    private EditorPlugin _pluginBase = null!;
    private PluginContext _context = null!;

    [BeforeTest]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "show-hide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _pluginBase = new EditorPlugin();
        _context = new PluginContext(null, _pluginBase, new NullLogger()) { Slug = "plugin", Directory = _root };
    }

    [AfterTest]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        if (GodotObject.IsInstanceValid(_pluginBase)) _pluginBase.Free();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }

    [TestCase]
    public void ShowAndHideRenameEitherNamedForm()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".src"));
        // Recipes name the hidden form, but the visible one means the same directory.
        ShowHideHelper.ShowDirectory(_context, Path.Combine(_root, "src"));
        Assertions.AssertBool(Directory.Exists(Path.Combine(_root, "src"))).IsTrue();
        Assertions.AssertBool(Directory.Exists(Path.Combine(_root, ".src"))).IsFalse();
        ShowHideHelper.HideDirectory(_context, Path.Combine(_root, ".src"));
        Assertions.AssertBool(Directory.Exists(Path.Combine(_root, ".src"))).IsTrue();
        Assertions.AssertBool(Directory.Exists(Path.Combine(_root, "src"))).IsFalse();
    }

    [TestCase]
    public void DirectoryAlreadyInTheWantedStateOrMissingIsNoFailure()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".src"));
        ShowHideHelper.HideDirectory(_context, Path.Combine(_root, ".src"));
        Assertions.AssertBool(Directory.Exists(Path.Combine(_root, ".src"))).IsTrue();
        ShowHideHelper.ShowDirectory(_context, Path.Combine(_root, ".src"));
        ShowHideHelper.ShowDirectory(_context, Path.Combine(_root, ".src"));
        Assertions.AssertBool(Directory.Exists(Path.Combine(_root, "src"))).IsTrue();
        ShowHideHelper.HideDirectory(_context, Path.Combine(_root, ".missing"));
        ShowHideHelper.ShowDirectory(_context, Path.Combine(_root, ".missing"));
        Assertions.AssertBool(Directory.Exists(Path.Combine(_root, "missing"))).IsFalse();
    }

    [TestCase]
    public void BothFormsExistingFailsWithoutRenamingEither()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".src"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Assertions.AssertThrown(() => ShowHideHelper.ShowDirectory(_context, Path.Combine(_root, ".src")))
            .IsInstanceOf<InvalidOperationException>();
        Assertions.AssertThrown(() => ShowHideHelper.HideDirectory(_context, Path.Combine(_root, ".src")))
            .IsInstanceOf<InvalidOperationException>();
        Assertions.AssertBool(Directory.Exists(Path.Combine(_root, ".src")) && Directory.Exists(Path.Combine(_root, "src"))).IsTrue();
    }

    [TestCase]
    public void ThePluginsOwnDirectoryAndTheOnesHoldingItAreRefused()
    {
        var addon = Path.Combine(_root, "addons", "plugin");
        Directory.CreateDirectory(addon);
        var context = new PluginContext(null, _pluginBase, new NullLogger()) { Slug = "plugin", Directory = addon };

        // e.g. AddDirectory(this.GetPluginDirectory()) or AddDirectory("res://addons"); hiding one leaves it, so the
        // recipe can still be uninstalled
        foreach (var directory in new[] { addon, Path.Combine(_root, "addons"), _root, Path.Combine(_root, "addons", ".plugin"), addon + "/src/.." })
        {
            Assertions.AssertThrown(() => ShowHideHelper.ShowDirectory(context, directory)).IsInstanceOf<InvalidOperationException>()
                .HasMessage($"The managed directory {directory} is the plugin's own directory or holds it. Only directories inside the plugin can be added with AddDirectory.");
            ShowHideHelper.HideDirectory(context, directory);
        }
        Assertions.AssertBool(Directory.Exists(addon)).IsTrue();
        Assertions.AssertBool(Directory.Exists(Path.Combine(_root, "addons", ".plugin"))).IsFalse();
    }

    [TestCase]
    public void DirectoriesOutsideThePluginAreRefusedWhenShownAndLeftWhenHidden()
    {
        var addon = Path.Combine(_root, "addons", "plugin");
        Directory.CreateDirectory(addon);
        var log = new List<string>();
        var context = new PluginContext(null, _pluginBase, new RecordingLogger(log, "plugin")) { Slug = "plugin", Directory = addon };
        var sibling = Path.Combine(_root, "addons", "plugin_data");
        var other = Path.Combine(_root, "addons", "other", ".src");
        Directory.CreateDirectory(Path.Combine(_root, "addons", ".plugin_data"));
        Directory.CreateDirectory(Path.Combine(_root, "addons", "other", "src"));

        // a sibling of the plugin, another plugin's directory, one reached through '..', and the project's own
        foreach (var directory in new[] { sibling, other, addon + "/../other/src", "res://.godot" })
        {
            Assertions.AssertThrown(() => ShowHideHelper.ShowDirectory(context, directory)).IsInstanceOf<InvalidOperationException>()
                .HasMessage($"The managed directory {directory} is outside the plugin's directory. Only directories inside the plugin can be added with AddDirectory.");
            ShowHideHelper.HideDirectory(context, directory);
        }

        Assertions.AssertBool(Directory.Exists(Path.Combine(_root, "addons", ".plugin_data"))).IsTrue();
        Assertions.AssertBool(Directory.Exists(Path.Combine(_root, "addons", "other", "src"))).IsTrue();
        Assertions.AssertInt(log.Count(l => l.StartsWith("warn: plugin: Not hiding the managed directory", StringComparison.Ordinal))).IsEqual(4);

        // inside the plugin, also through '..'
        Directory.CreateDirectory(Path.Combine(addon, ".src"));
        ShowHideHelper.ShowDirectory(context, addon + "/tools/../.src");
        Assertions.AssertBool(Directory.Exists(Path.Combine(addon, "src"))).IsTrue();
    }

    [TestCase]
    public void TheSameDirectoryMayBeNamedInEitherFormOrRelativeToTheProject()
    {
        var project = ProjectSettings.GlobalizePath("res://");
        Assertions.AssertBool(ShowHideHelper.SameDirectory(Path.Combine(_root, ".src"), Path.Combine(_root, "src") + "/")).IsTrue();
        Assertions.AssertBool(ShowHideHelper.SameDirectory("addons/plugin/.src", "res://addons/plugin/src")).IsTrue();
        Assertions.AssertBool(ShowHideHelper.SameDirectory("addons/plugin/src", Path.Combine(project, "addons", "plugin", ".src"))).IsTrue();
        Assertions.AssertBool(ShowHideHelper.SameDirectory(Path.Combine(_root, ".src"), Path.Combine(_root, ".tools"))).IsFalse();
    }
}
#endif
