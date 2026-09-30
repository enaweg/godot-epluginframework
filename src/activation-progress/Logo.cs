namespace ActivationProgress;

internal static class Logo
{
    // 150 logical pixels is approximately 4 cm at the standard desktop scale of 96 DPI.
    public const int Size = 150;

    public static byte[] Load()
    {
        using var resource = typeof(Logo).Assembly.GetManifestResourceStream("ActivationProgress.Logo.png")
            ?? throw new InvalidOperationException("The ePlugin logo is missing.");
        using var data = new MemoryStream();
        resource.CopyTo(data);
        return data.ToArray();
    }
}
