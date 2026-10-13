using System.Diagnostics;

namespace ActivationProgress;

/// <summary>
/// The window's opening: the logo fades in, pauses, then its two parts move apart while the panel grows between
/// them, and once the panel has its full size its controls fade in. Closing plays the same timeline backwards, from
/// wherever the opening got to, skipping the logo pause. Not thread-safe: each window uses it on its UI thread only.
/// </summary>
internal sealed class Animation
{
    public const double FadeSeconds = 0.5;
    public const double LogoPauseSeconds = 1.0;
    public const double SplitSeconds = 0.6;
    public const double ControlsSeconds = 0.5;
    public const double TotalSeconds = FadeSeconds + LogoPauseSeconds + SplitSeconds + ControlsSeconds;

    /// <summary>How often a window redraws while animating, and for its own indeterminate progress bar.</summary>
    public const int FrameMilliseconds = 16;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _from;
    private double _since;
    private int _direction;

    /// <summary>Whether the window has fully closed again.</summary>
    public bool Closed => _direction < 0 && Position <= 0;

    /// <summary>Seconds into the opening timeline.</summary>
    public double Position
    {
        get
        {
            var position = _from + _direction * (_clock.Elapsed.TotalSeconds - _since);
            // Closing moves straight from the joined logo to its fade-out.
            var pauseEnd = FadeSeconds + LogoPauseSeconds;
            if (_direction < 0 && _from > pauseEnd && position <= pauseEnd) position -= LogoPauseSeconds;
            return Math.Clamp(position, 0, TotalSeconds);
        }
    }

    public Frame Frame => Frame.At(Position);

    public void Open() => Turn(1);

    public void Close() => Turn(-1);

    private void Turn(int direction)
    {
        _from = Position;
        // An opening interrupted during its pause should also begin fading out immediately.
        if (direction < 0 && _from > FadeSeconds && _from <= FadeSeconds + LogoPauseSeconds) _from = FadeSeconds;
        _since = _clock.Elapsed.TotalSeconds;
        _direction = direction;
    }
}

/// <summary>
/// Whether the window is shown, and the delayed show it waits for. A closed window can be opened again: while it still
/// plays its closing it turns back, once hidden it shows after the show delay like a new one. Not thread-safe: each
/// window uses it on its UI thread only.
/// </summary>
internal sealed class Visibility(Animation animation)
{
    private bool _pending;

    /// <summary>Whether the window is on screen, including while it plays its opening or closing.</summary>
    public bool Visible { get; private set; }

    /// <summary>Identifies the latest scheduled show, so an earlier one that was canceled does nothing.</summary>
    public int Generation { get; private set; }

    /// <summary>Returns the generation to show after the show delay, or null when nothing needs to be scheduled.</summary>
    public int? Open()
    {
        if (Visible)
        {
            animation.Open();
            return null;
        }

        if (_pending) return null;
        _pending = true;
        return ++Generation;
    }

    /// <summary>Whether the scheduled show of <paramref name="generation"/> should put the window on screen now.</summary>
    public bool Show(int generation)
    {
        if (!_pending || generation != Generation) return false;
        _pending = false;
        Visible = true;
        animation.Open();
        return true;
    }

    /// <summary>Starts closing; returns true when the window is already hidden, e.g. closed before it was shown.</summary>
    public bool Close()
    {
        if (Visible)
        {
            animation.Close();
            return false;
        }

        _pending = false;
        return true;
    }

    /// <summary>Called every frame: returns true once, when the closing has finished and the window should hide.</summary>
    public bool Hide()
    {
        if (!Visible || !animation.Closed) return false;
        Visible = false;
        return true;
    }
}

/// <param name="Opacity">The whole window's opacity.</param>
/// <param name="Split">How far the logo's parts have moved apart, from 0 (whole logo) to 1 (final layout).</param>
/// <param name="Controls">The opacity of the panel's label and progress bar.</param>
internal readonly record struct Frame(double Opacity, double Split, double Controls)
{
    public static Frame At(double position)
    {
        return new Frame(
            Ease(position / Animation.FadeSeconds),
            Ease((position - Animation.FadeSeconds - Animation.LogoPauseSeconds) / Animation.SplitSeconds),
            Ease((position - Animation.FadeSeconds - Animation.LogoPauseSeconds - Animation.SplitSeconds) / Animation.ControlsSeconds));
    }

    private static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }
}

/// <summary>A rectangle in logical pixels (96 DPI), with a top-left origin.</summary>
internal readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public Box Scaled(double factor) => new(X * factor, Y * factor, Width * factor, Height * factor);
}

/// <summary>
/// Where everything is, in logical pixels inside the window, which has its final size from the start so it never
/// moves or resizes: the logo's parts start where they are in the logo, centered in the window, and end on either
/// side of the panel, the Godot head covering the panel's left edge and the puzzle piece's knob plugged into its
/// right edge.
/// </summary>
internal static class Layout
{
    public const double PanelWidth = 600;
    public const double PanelHeight = PanelWidth * 9 / 32;
    public const double PanelRadius = 16;
    public const double Padding = 28;
    public const double LabelHeight = 40;
    public const double BarHeight = 36;

    // Room for anti-aliased edges and, on macOS, the shadow.
    private const double Margin = 16;

    // The panel's center line, in logo coordinates: through the puzzle's left knob and the head's notch for it.
    private const double PanelCenterY = 0.62 * Logo.Size;

    // Fractions of each part's own width: the panel's left edge lies in the head's notch for the knob, and its
    // right edge where the puzzle's body begins.
    private const double HeadCovers = 0.2;
    private const double PuzzleKnob = 0.21;

    private static readonly Box Head = Logo.Head.Scaled(Logo.Size);
    private static readonly Box Puzzle = Logo.Puzzle.Scaled(Logo.Size);

    private static readonly double Left = Head.Width * (1 - HeadCovers);
    private static readonly double Right = Puzzle.Width * (1 - PuzzleKnob);
    private static readonly double Top = Math.Min(Math.Min(Head.Y, Puzzle.Y), PanelCenterY - PanelHeight / 2);
    private static readonly double Bottom = Math.Max(Math.Max(Head.Bottom, Puzzle.Bottom), PanelCenterY + PanelHeight / 2);

    public static double Width { get; } = Math.Ceiling(Left + PanelWidth + Right + 2 * Margin);
    public static double Height { get; } = Math.Ceiling(Bottom - Top + 2 * Margin);

    // Moves logo coordinates into the window, with the whole logo centered.
    private static readonly double LogoX = (Width - (Head.X + Puzzle.Right)) / 2;
    private static readonly double LogoY = Margin - Top;

    /// <summary>The panel once it has its full size.</summary>
    public static Box FullPanel { get; } = new(Margin + Left, LogoY + PanelCenterY - PanelHeight / 2, PanelWidth, PanelHeight);

    // The controls keep clear of the parts that overlap the panel.
    private static readonly double ContentLeft = FullPanel.X + Head.Width * HeadCovers + Padding / 2;
    private static readonly double ContentWidth = FullPanel.Right - Puzzle.Width * PuzzleKnob - Padding / 2 - ContentLeft;

    public static Box Label { get; } = new(ContentLeft, FullPanel.Y + Padding, ContentWidth, LabelHeight);

    public static Box Bar { get; } = new(ContentLeft, FullPanel.Bottom - Padding - BarHeight, ContentWidth, BarHeight);

    public static Box HeadAt(double split)
    {
        var final = FullPanel.X - Left;
        return Head with { X = Lerp(LogoX + Head.X, final, split), Y = LogoY + Head.Y };
    }

    public static Box PuzzleAt(double split)
    {
        var final = FullPanel.Right - Puzzle.Width * PuzzleKnob;
        return Puzzle with { X = Lerp(LogoX + Puzzle.X, final, split), Y = LogoY + Puzzle.Y };
    }

    /// <summary>The panel grows from nothing, centered between the parts; null while it has no width.</summary>
    public static Box? PanelAt(double split)
    {
        if (split <= 0) return null;
        var head = HeadAt(split);
        var puzzle = PuzzleAt(split);
        var center = (head.X + Left + puzzle.X + Puzzle.Width * PuzzleKnob) / 2;
        var width = PanelWidth * split;
        return FullPanel with { X = center - width / 2, Width = width };
    }

    /// <summary>A window size or position in device pixels.</summary>
    public static int Scale(double value, double scale) => (int)Math.Round(value * scale);

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;
}
