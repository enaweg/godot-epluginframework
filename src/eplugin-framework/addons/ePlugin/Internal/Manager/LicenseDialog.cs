#if TOOLS
using System;
using System.Linq;
using Enaweg.Plugin.Internal.Licenses;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Asks to accept or decline the licenses plugins need before they are enabled or updated. Several licenses are
/// listed on the left and can be accepted at once. A viewer shows a single license again, e.g. from the ePlugin
/// Manager's plugin details. The layout lives in LicenseDialog.tscn.
/// </summary>
[Tool]
internal sealed partial class LicenseDialog : AcceptDialog, ISerializationListener
{
    internal const string ScenePath = "res://addons/ePlugin/Internal/Manager/LicenseDialog.tscn";
    private const string DeclineAction = "decline", AcceptAllAction = "accept_all";
    private readonly EditorSignalConnections _signals = new();
    private LicenseReview _review = null!;
    private Action<LicenseReview>? _finished;
    private ItemList _list = null!;
    private Label _summary = null!;
    private Label _name = null!;
    private Label _source = null!;
    private Label _neededBy = null!;
    private RichTextLabel _text = null!;
    private Button? _decline;
    private Button? _acceptAll;
    private string? _selected;
    private bool _done;
    private bool ViewOnly => _finished is null;

    /// <summary>Calls <paramref name="finished"/> once every license is decided; closing the dialog declines the rest.</summary>
    public static LicenseDialog Create(LicenseReview review, Action<LicenseReview> finished)
    {
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<LicenseDialog>();
        dialog.Initialize(review, finished, Summary(review));
        return dialog;
    }

    /// <summary>Shows a license to read it again; nothing is accepted or declined.</summary>
    public static LicenseDialog CreateViewer(LicenseInfo license)
    {
        var dialog = GD.Load<PackedScene>(ScenePath).Instantiate<LicenseDialog>();
        dialog.Initialize(new LicenseReview([license.Entry], []), null, ViewSummary(license));
        return dialog;
    }

    /// <summary>Opens above whatever exclusive editor window is open, e.g. Project Settings or the ePlugin Manager.</summary>
    public void Open() => EditorInterface.Singleton.PopupDialogCenteredClamped(this, Size, 0.9f);

    private void Initialize(LicenseReview review, Action<LicenseReview>? finished, string summary)
    {
        _review = review; _finished = finished;
        _list = GetNode<ItemList>("%LicenseList"); _summary = GetNode<Label>("%Summary");
        _name = GetNode<Label>("%LicenseName"); _source = GetNode<Label>("%LicenseSource");
        _neededBy = GetNode<Label>("%NeededBy"); _text = GetNode<RichTextLabel>("%LicenseText");
        EditorWindows.Prepare(this, _signals, this, nameof(QueueWindowIcon));
        if (ViewOnly)
        {
            Title = "ePlugin — License";
            OkButtonText = "Close";
            _signals.Connect(this, AcceptDialog.SignalName.Confirmed, Callable.From(Finish));
            _signals.Connect(this, AcceptDialog.SignalName.Canceled, Callable.From(Finish));
        }
        else
        {
            _decline = AddButton("Decline", false, DeclineAction);
            _acceptAll = AddButton("Accept all", true, AcceptAllAction);
            _acceptAll.TooltipText = "Accept every license that is not decided yet.";
            _signals.Connect(this, AcceptDialog.SignalName.Confirmed, Callable.From(() => Decide(accept: true)));
            _signals.Connect(this, AcceptDialog.SignalName.CustomAction, Callable.From<StringName>(HandleCustomAction));
            _signals.Connect(this, AcceptDialog.SignalName.Canceled, Callable.From(DeclineRemaining));
        }
        _signals.Connect(_list, ItemList.SignalName.ItemSelected, Callable.From<long>(index => Display(_review.Entries[(int)index].Slug)));
        _signals.Connect(_text, RichTextLabel.SignalName.MetaClicked, Callable.From<Variant>(meta =>
        {
            if (PluginManagerViewModel.IsWebUrl(meta.AsString())) OS.ShellOpen(meta.AsString());
        }));
        _summary.Text = summary;
        _list.Visible = review.Entries.Count > 1;
        Display(review.NextPending()?.Slug ?? review.Entries[0].Slug);
    }

    private static string Summary(LicenseReview review)
    {
        var first = review.Entries[0];
        if (review.IsUpdate)
            return review.Entries.Count == 1
                ? $"The update of {first.Name} comes with a license you have not accepted yet. Accept it to install the update."
                : "These updates come with licenses you have not accepted yet. Updates whose license you decline are not installed.";
        if (review.Activations.All(a => a.Enabled))
            return review.Entries.Count == 1
                ? $"{first.Name} is enabled, but its license is not accepted as it is now, for example because an update changed it. " +
                  $"Accept it to keep {first.Name} enabled; declining disables it."
                : "These plugins are enabled, but their licenses are not accepted as they are now, for example because an update " +
                  "changed them. Plugins whose license you decline are disabled.";
        if (review.Activations.Any(a => a.Enabled))
            return "Accept the licenses below. Plugins whose license you decline are not enabled, or disabled when they are enabled already.";
        if (review.Entries.Count == 1 && review.Activations.Count == 1 && review.Activations[0].Slug == first.Slug)
            return $"{first.Name} asks you to accept its license before it is enabled. If you decline, it stays disabled.";
        return $"Enabling {string.Join(", ", review.Activations.Select(a => a.Name))} needs these licenses to be accepted, " +
               "including those of the dependencies enabled with it. Plugins whose licenses you decline stay disabled.";
    }

    private static string ViewSummary(LicenseInfo license)
    {
        var name = license.Entry.Name;
        if (!license.Required) return $"{name} does not ask for its license to be accepted.";
        if (license.IsAccepted)
            return $"The license of {name} was accepted on {license.Accepted!.AcceptedUtc.ToLocalTime():g}" +
                   (license.Accepted.Automatic ? $", automatically because {LicenseSettings.AutoAcceptKey} was enabled." : ".");
        return license.Accepted is null
            ? $"The license of {name} is not accepted yet. It is asked for when the plugin is enabled."
            : $"This license of {name} is not accepted yet; an earlier one was. It is asked for when the plugin is enabled or updated.";
    }

    private void Display(string slug)
    {
        _selected = slug;
        var entry = _review.Entries.First(e => e.Slug == slug);
        _name.Text = string.IsNullOrWhiteSpace(entry.Version) ? entry.Name : $"{entry.Name} {entry.Version}";
        _source.Text = PluginLicense.Display(entry.Slug, entry.Source);
        _source.TooltipText = _source.Text;
        // the license is BBCode; a problem is plain text
        _text.Text = entry.Problem is null ? entry.Text : $"[color=#ff7070]{entry.Problem.Replace("[", "[lb]")}[/color]";
        _text.ScrollToLine(0);
        var neededBy = _review.NeededBy(slug).Select(a => a.Name).ToArray();
        _neededBy.Text = _review.IsUpdate || neededBy.Length == 0 ? "" : $"Needed to enable {string.Join(", ", neededBy)}.";
        _neededBy.Visible = _neededBy.Text.Length > 0;
        Render();
    }

    private void Render()
    {
        var theme = EditorInterface.Singleton.GetEditorTheme();
        Texture2D? Icon(string name) => theme is not null && theme.HasIcon(name, "EditorIcons") ? theme.GetIcon(name, "EditorIcons") : null;
        _list.Clear();
        foreach (var entry in _review.Entries)
        {
            var decision = _review.DecisionOf(entry.Slug);
            var index = _list.AddItem(entry.Name, decision switch
            {
                LicenseDecision.Accepted => Icon("StatusSuccess"),
                LicenseDecision.Declined => Icon("StatusError"),
                _ => null
            });
            _list.SetItemTooltip(index, decision switch
            {
                LicenseDecision.Accepted => "Accepted.",
                LicenseDecision.Declined => "Declined.",
                LicenseDecision.Skipped => "Not needed anymore: the plugins that need it stay disabled.",
                _ => "Not decided yet."
            });
            if (decision == LicenseDecision.Skipped)
                _list.SetItemCustomFgColor(index, theme?.GetColor("font_disabled_color", "Button") ?? new Color(0.5f, 0.5f, 0.5f));
            if (entry.Slug == _selected) _list.Select(index);
        }

        if (_decline is null || _acceptAll is null) return;
        var pending = _selected is not null && _review.DecisionOf(_selected) == LicenseDecision.Pending;
        GetOkButton().Disabled = _decline.Disabled = !pending;
        _acceptAll.Visible = _review.PendingCount > 1;
    }

    private void HandleCustomAction(StringName action)
    {
        if (action == DeclineAction) Decide(accept: false);
        else if (action == AcceptAllAction) { _review.AcceptAll(); Advance(); }
    }

    private void Decide(bool accept)
    {
        if (_selected is null || _review.DecisionOf(_selected) != LicenseDecision.Pending) return;
        if (accept) _review.Accept(_selected);
        else _review.Decline(_selected);
        Advance();
    }

    private void Advance()
    {
        if (_review.IsComplete) Finish();
        else Display(_review.NextPending(_selected)!.Slug);
    }

    private void DeclineRemaining()
    {
        if (_done) return;
        _review.DeclineRemaining();
        Finish();
    }

    private void Finish()
    {
        if (_done) return;
        _done = true; Hide(); _finished?.Invoke(_review); QueueFree();
    }

    private void QueueWindowIcon() => CallDeferred(nameof(RefreshWindowIcon));
    private void RefreshWindowIcon() => EditorWindows.ApplyIcon(this);

    // An assembly reload drops the review, so the dialog goes with it. The plugins it was asked for stay disabled.
    public void OnBeforeSerialize()
    {
        _signals.Dispose();
        if (_done) return;
        _done = true; Hide(); QueueFree();
    }

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
