namespace ActivationProgress;

/// <summary>
/// The ePlugin logo in its two parts: the Godot head and the C# puzzle piece. <c>design/icon-head.png</c> and
/// <c>design/icon-puzzle.png</c> are <c>design/icon-nobg.png</c> cut along the puzzle path of
/// <c>design/icon.svg</c>, scaled to 450 pixels and cropped to each part.
/// </summary>
internal static class Logo
{
    // 300 logical pixels is approximately 8 cm at the standard desktop scale of 96 DPI.
    public const int Size = 300;

    /// <summary>Where each part lies in the whole logo, as fractions of its size.</summary>
    public static readonly Box Head = new(8 / 450.0, 90 / 450.0, 277 / 450.0, 270 / 450.0);

    public static readonly Box Puzzle = new(231 / 450.0, 156 / 450.0, 211 / 450.0, 200 / 450.0);

    public static byte[] LoadHead() => Load("ActivationProgress.LogoHead.png");

    public static byte[] LoadPuzzle() => Load("ActivationProgress.LogoPuzzle.png");

    private static byte[] Load(string name)
    {
        using var resource = typeof(Logo).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The ePlugin logo is missing.");
        using var data = new MemoryStream();
        resource.CopyTo(data);
        return data.ToArray();
    }
}
