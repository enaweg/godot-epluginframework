using System.Text;

namespace ActivationProgress;

internal static class Program
{
    private const string Title = "ePlugin Framework";
    private const string InitialText = "Working...";

    // Operations that finish sooner close the helper before its window ever appears.
    private const int ShowDelayMilliseconds = 300;

    [STAThread]
    private static int Main()
    {
        IProgressWindow window;
        if (OperatingSystem.IsWindows())
        {
            window = new Win32ProgressWindow();
        }
        else if (OperatingSystem.IsMacOS())
        {
            window = new CocoaProgressWindow();
        }
        else if (OperatingSystem.IsLinux())
        {
            window = new GtkProgressWindow();
        }
        else
        {
            return 1;
        }

        try
        {
            window.Run(Title, InitialText, ShowDelayMilliseconds, () =>
            {
                var output = new StreamWriter(Console.OpenStandardOutput(), Encoding.ASCII) { AutoFlush = true };
                output.WriteLine("READY");

                // Reading a redirected console stream can block before its first await.
                new Thread(() => ReadCommands(window)) { IsBackground = true }.Start();
            });
        }
        catch (Exception)
        {
            // Missing native UI libraries (e.g. no GTK 3); the host continues without a window.
            return 1;
        }

        return 0;
    }

    private static void ReadCommands(IProgressWindow window)
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.ASCII);
            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                if (line == "CLOSE")
                {
                    break;
                }

                if (line.StartsWith("TEXT ", StringComparison.Ordinal))
                {
                    try
                    {
                        window.SetText(Encoding.UTF8.GetString(Convert.FromBase64String(line[5..])));
                    }
                    catch (FormatException)
                    {
                        // Ignore malformed optional text updates.
                    }
                }
            }
        }
        catch (IOException)
        {
            // The parent process closed the pipe.
        }

        // The native UI loops own the main thread; exiting from here avoids per-platform shutdown plumbing.
        Environment.Exit(0);
    }
}

internal interface IProgressWindow
{
    /// <summary>
    /// Creates the window hidden and runs the native UI loop on the calling thread until the window is closed.
    /// <paramref name="ready"/> runs on the UI thread once the window exists. The window is shown without taking
    /// focus from the editor after <paramref name="showDelayMilliseconds"/>.
    /// </summary>
    void Run(string title, string text, int showDelayMilliseconds, Action ready);

    /// <summary>Replaces the label text. Callable from any thread once <c>ready</c> has run.</summary>
    void SetText(string text);
}
