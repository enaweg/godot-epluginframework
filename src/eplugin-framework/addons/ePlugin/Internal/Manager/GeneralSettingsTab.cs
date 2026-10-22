#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// The General tab of the settings: ePlugin's project settings, the ones Project Settings lists under ePlugin. The layout
/// lives in SettingsDialog.tscn, where each CheckBox, SpinBox and OptionButton names its setting in the
/// <see cref="SettingMeta"/> metadata, and an OptionButton the value of each item in <see cref="ValuesMeta"/>. A change is
/// saved to project.godot right away, like in Project Settings.
/// </summary>
[Tool]
internal sealed partial class GeneralSettingsTab : VBoxContainer, ISerializationListener
{
    internal const string SettingMeta = "setting", ValuesMeta = "values";
    private readonly EditorSignalConnections _signals = new();
    private Control[] _controls = [];
    private CheckBox _autoAccept = null!;
    private ConfirmationDialog _autoAcceptConfirm = null!;
    private Label _autoAcceptWarning = null!;
    private Label _status = null!;

    /// <summary>Raised after a setting was changed here.</summary>
    public event Action? Changed;

    public void Initialize()
    {
        _autoAccept = GetNode<CheckBox>("%AutoAcceptCheck"); _autoAcceptConfirm = GetNode<ConfirmationDialog>("%AutoAcceptConfirm");
        _autoAcceptWarning = GetNode<Label>("%AutoAcceptWarning"); _status = GetNode<Label>("%GeneralStatus");
        _controls = SettingControls(this).ToArray();
        foreach (var control in _controls)
        {
            switch (control)
            {
                case CheckBox check when check == _autoAccept:
                    _signals.Connect(check, BaseButton.SignalName.Toggled, Callable.From<bool>(ToggleAutoAccept));
                    break;
                case CheckBox check:
                    _signals.Connect(check, BaseButton.SignalName.Toggled, Callable.From<bool>(value => Save(check, value)));
                    break;
                case SpinBox spin:
                    _signals.Connect(spin, Godot.Range.SignalName.ValueChanged, Callable.From<double>(value => Save(spin, (int)value)));
                    break;
                case OptionButton option:
                    _signals.Connect(option, OptionButton.SignalName.ItemSelected, Callable.From<long>(index => Save(option, Values(option)[(int)index])));
                    break;
            }
        }
        _signals.Connect(_autoAcceptConfirm, AcceptDialog.SignalName.Confirmed, Callable.From(() =>
        {
            _autoAccept.SetPressedNoSignal(true);
            Save(_autoAccept, true);
        }));
    }

    /// <summary>The controls bound to a setting, in scene order.</summary>
    internal static IEnumerable<Control> SettingControls(Node parent)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is Control control && control.HasMeta(SettingMeta)) yield return control;
            foreach (var nested in SettingControls(child)) yield return nested;
        }
    }

    internal static string SettingOf(Control control) => control.GetMeta(SettingMeta).AsString();
    internal static string[] Values(OptionButton option) => option.GetMeta(ValuesMeta).AsStringArray();

    /// <summary>Reads every setting again; Project Settings or a pull may have changed them.</summary>
    public void Open()
    {
        _status.Visible = false;
        foreach (var control in _controls)
        {
            var value = ProjectSettings.GetSetting(SettingOf(control));
            switch (control)
            {
                case CheckBox check: check.SetPressedNoSignal(value.AsBool()); break;
                case SpinBox spin: spin.SetValueNoSignal(value.AsDouble()); break;
                // A value Project Settings does not offer either, e.g. one edited by hand, selects nothing.
                case OptionButton option: option.Select(Array.IndexOf(Values(option), value.AsString())); break;
            }
        }
        var theme = EditorInterface.Singleton.GetEditorTheme();
        _autoAcceptWarning.AddThemeColorOverride("font_color",
            theme is not null && theme.HasColor("warning_color", "Editor") ? theme.GetColor("warning_color", "Editor") : new Color(1f, 0.87f, 0.4f));
    }

    // Accepting every license unread is asked for first; turning it off is not.
    private void ToggleAutoAccept(bool value)
    {
        if (!value) { Save(_autoAccept, false); return; }
        _autoAccept.SetPressedNoSignal(false);
        _autoAcceptConfirm.PopupCentered();
    }

    private void Save(Control control, Variant value)
    {
        var key = SettingOf(control);
        ProjectSettings.SetSetting(key, value);
        var error = ProjectSettings.Save();
        _status.Text = error == Error.Ok ? "" : $"Cannot save {key} to project.godot: {error}.";
        _status.Visible = error != Error.Ok;
        Changed?.Invoke();
    }

    // Reload serializes every script before disposing any of them, so callback targets are still valid here.
    public void OnBeforeSerialize() => _signals.Dispose();
    public void OnAfterDeserialize() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _signals.Dispose();
        base.Dispose(disposing);
    }

    public override void _ExitTree()
    {
        _signals.Dispose();
        base._ExitTree();
    }
}
#endif
