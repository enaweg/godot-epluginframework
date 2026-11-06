#if TOOLS
using System;
using Enaweg.Plugin.Internal.Update;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Asks whether to clean up a local directory whose plugin packages take more space than the user set. Cleaning up
/// opens the Tools tab of the settings; nothing is deleted from here. The layout lives in CleanupSuggestionDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class CleanupSuggestionDialog : ConfirmationDialog, ISerializationListener
{
    internal const string ScenePath = "res://addons/ePlugin/Internal/Manager/CleanupSuggestionDialog.tscn";
    private readonly DialogLifetime _lifetime = new();
    private Action<bool>? _decided;

    /// <summary>
    /// Shows <paramref name="suggestion"/>; <paramref name="decided"/> is called with whether to clean up once the dialog
    /// is closed, but not when an assembly reload removes it.
    /// </summary>
    public static CleanupSuggestionDialog Create(CleanupSuggestion suggestion, Action<bool> decided)
    {
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<CleanupSuggestionDialog>();
        dialog.Initialize(suggestion, decided);
        return dialog;
    }

    /// <summary>Opens above whatever exclusive editor window is open, e.g. Project Settings or the ePlugin Manager.</summary>
    public void Open() => EditorWindows.Popup(this);

    private void Initialize(CleanupSuggestion suggestion, Action<bool> decided)
    {
        _decided = decided;
        GetNode<Label>("%CleanupMessage").Text = Message(suggestion);
        _lifetime.Prepare(this, MethodName.RefreshWindowIcons);
        _lifetime.Signals.Connect(this, AcceptDialog.SignalName.Confirmed, Callable.From(() => Finish(true)));
        _lifetime.Signals.Connect(this, AcceptDialog.SignalName.Canceled, Callable.From(() => Finish(false)));
    }

    internal static string Message(CleanupSuggestion suggestion) =>
        $"The plugin packages in your local directory {suggestion.Directory} take {LocalCleanup.FormatSize(suggestion.Bytes)}, " +
        $"more than the {LocalCleanup.FormatSize(suggestion.Limit)} above which you want to clean it up. Clean it up now?";

    private void Finish(bool cleanUp) => _lifetime.Finish(() => _decided?.Invoke(cleanUp));

    private void RefreshWindowIcons() => _lifetime.RefreshIcons();

    public void OnBeforeSerialize() => _lifetime.ReleaseForReload();
    public void OnAfterDeserialize() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _lifetime.ReleaseCallbacks();
        base.Dispose(disposing);
    }

    public override void _ExitTree()
    {
        _lifetime.ReleaseCallbacks();
        base._ExitTree();
    }
}
#endif
