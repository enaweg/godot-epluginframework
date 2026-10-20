#if TOOLS
using System;
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
}
#endif
