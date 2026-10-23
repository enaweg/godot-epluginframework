#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal;
using Enaweg.Plugin.Internal.Dotnet;
using Enaweg.Plugin.Logging;
using Godot;
using Moq;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// An <see cref="EGlobal"/> with a fake editor, for tests of the plugin lifecycle. Plugins live in a temporary addons
/// folder; enabling or disabling one runs its callbacks like Godot does, inside the toggle. Recipe operations are
/// recorded in <see cref="Operations"/> in the order they ran, and the state files are real.
/// </summary>
internal sealed class LifecycleFixture : IDisposable
{
    private readonly Dictionary<string, Action<IEEditorPluginBuilder>?> _ePlugins = new(StringComparer.Ordinal);
    private readonly List<GodotObject> _objects = [];
    private readonly GenericLoggerFactory _loggers;

    public LifecycleFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "eplugin-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "addons"));
        Host = new FakeEditorHost(this);
        Cli = new RecordingCli(Operations);
        Global = new EGlobal(Host);
        _loggers = new GenericLoggerFactory(category => new RecordingLogger(Log, category));
        Store = new PluginStateStore(Path.Combine(Root, "addons", "eplugin-state.json"), new RecordingLogger(Log, "store"));
        Store.Load();
        Store.TryCreateBaseline([]);
        Global.StateStore = Store;
        Framework = new EPluginPlugin();
        _objects.Add(Framework);
        Global.Attach(Framework, _loggers, Cli);
    }

    public string Root { get; }
    public FakeEditorHost Host { get; }
    public EGlobal Global { get; }
    public PluginStateStore Store { get; }
    public RecordingCli Cli { get; }

    /// <summary>The framework's own editor plugin, which builds the solution.</summary>
    public EPluginPlugin Framework { get; }

    /// <summary>The recipe operations and builds, e.g. <c>add-nuget a.Package</c>, <c>build</c>.</summary>
    public List<string> Operations { get; } = [];

    /// <summary>Everything logged, as <c>error|warn|log: message</c>.</summary>
    public List<string> Log { get; } = [];

    public string Addon(string slug) => Path.Combine(Root, "addons", slug);

    /// <summary>Adds a disabled ePlugin; <paramref name="recipe"/> declares its recipe, null for one that throws.</summary>
    public void AddEPlugin(string slug, Action<IEEditorPluginBuilder>? recipe, string version = "1.0.0", string extraConfig = "")
    {
        WriteConfig(slug, version, "Plugin.cs", extraConfig);
        _ePlugins[slug] = recipe;
    }

    /// <summary>Adds a disabled GDScript plugin.</summary>
    public void SetRecipe(string slug, Action<IEEditorPluginBuilder>? recipe) => _ePlugins[slug] = recipe;

    public void AddPlainPlugin(string slug, string version = "1.0.0", string extraConfig = "") =>
        WriteConfig(slug, version, "plugin.gd", extraConfig);

    public void Enable(string slug) => Host.SetPluginEnabled(slug, true);

    public void Disable(string slug) => Host.SetPluginEnabled(slug, false);

    /// <summary>The context of the plugin's current editor instance.</summary>
    public PluginContext? Context(string slug) => Global.Contexts.LastOrDefault(c => c.Slug == slug && c.Plugin is not null);

    public EEditorPluginState? State(string slug) => Context(slug)?.State;

    public bool IsEPlugin(string slug) => _ePlugins.ContainsKey(slug);

    /// <summary>ePlugins whose <c>_EnablePlugin</c> and <c>_DisablePlugin</c> throw before calling the framework.</summary>
    public HashSet<string> Silent { get; } = new(StringComparer.Ordinal);

    public void CreateRecipe(string slug, IEEditorPluginBuilder builder) =>
        (_ePlugins[slug] ?? throw new InvalidOperationException($"The recipe of {slug} fails."))(builder);

    /// <summary>Godot creates an instance and calls its <c>_EnablePlugin</c>, which calls <c>this.EnableEPlugin()</c>.</summary>
    public void OnEnabled(string slug)
    {
        if (!IsEPlugin(slug) || Silent.Contains(slug)) return;
        var pluginBase = new EditorPlugin();
        _objects.Add(pluginBase);
        var plugin = new Mock<IEEditorPlugin>();
        plugin.Setup(p => p.CreateRecipe(It.IsAny<IEEditorPluginBuilder>())).Callback<IEEditorPluginBuilder>(b => CreateRecipe(slug, b));
        var context = new PluginContext(plugin.Object, pluginBase, _loggers.CreateLogger(slug))
        {
            Slug = slug,
            Metadata = EditorPluginExtensions.ReadMetadata(Path.Combine(Addon(slug), "plugin.cfg")),
            Cli = Cli
        };
        Global.AddContext(context);
        Global.EnableEPlugin(context);
    }

    /// <summary>Godot calls <c>_DisablePlugin</c>, which calls <c>this.DisableEPlugin()</c>, and frees the instance.</summary>
    public void OnDisabled(string slug)
    {
        if (Silent.Contains(slug) || Context(slug) is not { } context) return;
        if (context.State != EEditorPluginState.Deactivated) Global.DisableEPlugin(context);
    }

    private void WriteConfig(string slug, string version, string script, string extraConfig)
    {
        Directory.CreateDirectory(Addon(slug));
        File.WriteAllText(Path.Combine(Addon(slug), "plugin.cfg"),
            $"[plugin]\n\nname=\"{slug}\"\nversion=\"{version}\"\nscript=\"{script}\"\n{extraConfig}\n");
    }

    public void Dispose()
    {
        // work a test left posted must not run in the next one
        EGlobal.RunPosted();
        EGlobal.ForgetPluginToggles();
        foreach (var obj in _objects.Where(GodotObject.IsInstanceValid)) obj.Free();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

/// <summary>The editor of a <see cref="LifecycleFixture"/>.</summary>
internal sealed class FakeEditorHost(LifecycleFixture fixture) : IEditorHost
{
    public HashSet<string> Enabled { get; } = new(StringComparer.Ordinal) { "ePlugin" };

    /// <summary>Plugins Godot refuses to enable, e.g. because their script does not compile.</summary>
    public HashSet<string> Broken { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Autoloads { get; } = new(StringComparer.Ordinal);

    public bool Dialogs { get; set; }

    public int Scans { get; private set; }

    /// <summary>The plugins whose <c>_EnablePlugin</c> is running.</summary>
    private readonly HashSet<string> _enabling = new(StringComparer.Ordinal);

    public bool IsPluginEnabled(string slug) => Enabled.Contains(slug);

    public void SetPluginEnabled(string slug, bool enabled)
    {
        // Godot cannot enable a plugin without plugin.cfg or with a script that does not load
        if (Enabled.Contains(slug) == enabled ||
            enabled && (Broken.Contains(slug) || !File.Exists(Path.Combine(fixture.Addon(slug), "plugin.cfg")))) return;
        // Godot would free the plugin while its own callback still runs
        if (!enabled && _enabling.Contains(slug))
            throw new InvalidOperationException($"{slug} is disabled inside its own _EnablePlugin.");
        if (enabled)
        {
            Enabled.Add(slug);
            _enabling.Add(slug);
            try { fixture.OnEnabled(slug); }
            finally { _enabling.Remove(slug); }
        }
        else
        {
            fixture.OnDisabled(slug);
            Enabled.Remove(slug);
        }
    }

    public IReadOnlySet<string> EnabledPluginSlugs() => Enabled.ToHashSet(StringComparer.Ordinal);

    /// <summary>The nodes an assembly reload finds the editor plugins among.</summary>
    public List<Node> Nodes { get; } = [];

    public string ProjectRoot => fixture.Root;

    public string AssemblyName => "";

    public string AddonsDirectory => Path.Combine(fixture.Root, "addons").Replace('\\', '/');

    /// <summary>The user's configuration folder, inside the fixture's temporary folder.</summary>
    public string ConfigDirectory => Path.Combine(fixture.Root, "config");

    public string PluginDirectory(string slug) => fixture.Addon(slug).Replace('\\', '/');

    public IReadOnlyList<Node> PluginNodes() => Nodes;

    public bool IsEPlugin(string slug) => fixture.IsEPlugin(slug);

    public bool CreateRecipeOfDisabledPlugin(string slug, IEEditorPluginBuilder builder)
    {
        if (!fixture.IsEPlugin(slug)) return false;
        fixture.CreateRecipe(slug, builder);
        return true;
    }

    public bool CanShowDialogs(Node framework) => Dialogs;

    public void ScanFilesystem() => Scans++;

    public bool IsScanningFilesystem { get; set; }

    /// <summary>How often "Import Resources When Unfocused" was turned off, and how often back on.</summary>
    public (int Suspended, int Restored) UnfocusedImports { get; private set; }

    public IDisposable SuspendUnfocusedImports()
    {
        UnfocusedImports = (UnfocusedImports.Suspended + 1, UnfocusedImports.Restored);
        return new Restore(() => UnfocusedImports = (UnfocusedImports.Suspended, UnfocusedImports.Restored + 1));
    }

    public int Restarts { get; private set; }

    public void RestartEditor() => Restarts++;

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    public string? GetAutoload(string name) => Autoloads.GetValueOrDefault(name);

    public void AddAutoload(EditorPlugin plugin, string name, string path) => Autoloads[Valid(plugin, name)] = path;

    public void RemoveAutoload(EditorPlugin plugin, string name) => Autoloads.Remove(Valid(plugin, name));

    /// <summary>Godot frees a plugin's instance when it is disabled; calling one that was freed throws.</summary>
    private static string Valid(EditorPlugin plugin, string name) => GodotObject.IsInstanceValid(plugin) ? name
        : throw new ObjectDisposedException(nameof(EditorPlugin), $"Changing autoload {name} through a freed plugin.");

    /// <summary>The <c>.gdextension</c> files Godot has loaded, as file system paths with '/'.</summary>
    public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsExtensionLoaded(string file) => Extensions.Contains(file.Replace('\\', '/'));

    public bool LoadExtension(string file) => Extensions.Add(file.Replace('\\', '/'));

    public bool UnloadExtension(string file) => Extensions.Remove(file.Replace('\\', '/'));
}

/// <summary>A dotnet CLI that records what it is asked to do and fails what it is told to.</summary>
internal sealed class RecordingCli(List<string> operations) : IDotnetCli, ICheckedDotnetCli
{
    public HashSet<string> FailingPackages { get; } = new(StringComparer.Ordinal);

    public bool BuildFails { get; set; }

    private bool Record(string operation)
    {
        operations.Add(operation);
        return true;
    }

    public bool AddNugetToProject(string nugetName, string? version = null, string? source = null, bool prerelease = false) =>
        Record($"add-nuget {nugetName}") && !FailingPackages.Contains(nugetName);

    public bool TryRemoveNugetFromProject(string nugetName) => Record($"remove-nuget {nugetName}");
    public bool TryAddProjectToSolution(string projectPath, string? virtualFolderName) => Record($"add-project {projectPath}");
    public bool TryRemoveProjectFromSolution(string projectPath) => Record($"remove-project {projectPath}");
    public bool TryAddProjectReference(string projectReference) => Record($"add-reference {projectReference}");
    public bool TryRemoveProjectReference(string projectReference) => Record($"remove-reference {projectReference}");
    public bool TryRebuildSolution() => Record("build") && !BuildFails;
    public BuildOutcome TryBuild() => new(TryRebuildSolution() ? 0 : 1, []);
    public BuildOutcome TryBuildPluginsOnly(PluginsOnlyBuild build) => new(Record("build plugins-only") ? 0 : 1, []);

    public void RebuildSolution() => TryRebuildSolution();
    public void RunTests() { }
    public void RemoveProjectFromSolution(string projectPath) => TryRemoveProjectFromSolution(projectPath);
    public void AddProjectToSolution(string projectPath, string? virtualFolderName = null) => TryAddProjectToSolution(projectPath, virtualFolderName);
    public void RemoveNugetFromProject(string nugetName) => TryRemoveNugetFromProject(nugetName);
    public void AddProjectReference(string projectReference) => TryAddProjectReference(projectReference);
    public void RemoveProjectReference(string projectReference) => TryRemoveProjectReference(projectReference);
    public (int, string[]) Execute(string command, string[] args) => (0, []);
}

internal sealed class RecordingLogger(List<string> log, string category) : ILogger
{
    public void Log(string message) => log.Add($"log: {category}: {message}");
    public void Warn(string message) => log.Add($"warn: {category}: {message}");
    public void Error(string message) => log.Add($"error: {category}: {message}");
}
#endif
