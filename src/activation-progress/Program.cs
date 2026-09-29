using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace ActivationProgress;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppBuilder.Configure<ProgressApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class ProgressApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        Styles.Add(new FluentTheme());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var output = new StreamWriter(Console.OpenStandardOutput(), Encoding.ASCII) { AutoFlush = true };
            var window = new ProgressWindow();
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                output.WriteLine("READY");
            }, DispatcherPriority.Background);

            // Reading a redirected console stream can block before its first await.
            _ = Task.Run(() => ReadCommands(window, desktop));
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ReadCommands(ProgressWindow window,
        IClassicDesktopStyleApplicationLifetime desktop)
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
                        var text = Encoding.UTF8.GetString(Convert.FromBase64String(line[5..]));
                        Dispatcher.UIThread.Post(() => window.SetText(text));
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

        Dispatcher.UIThread.Post(() => desktop.Shutdown());
    }
}

internal sealed class ProgressWindow : Window
{
    private readonly TextBlock _text = new() { Text = "Working..." };

    public ProgressWindow()
    {
        Title = "ePlugin Framework";
        Width = 340;
        Height = 100;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(20),
            Children =
            {
                _text,
                new ProgressBar
                {
                    IsIndeterminate = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                }
            }
        };
    }

    public void SetText(string text)
    {
        _text.Text = text;
    }
}
