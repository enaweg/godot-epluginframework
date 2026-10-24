using System.Text;
using Xunit;

namespace ActivationProgress.Tests;

/// <summary>How the helper turns command lines from editors into the window's state, with time driven by the test.</summary>
public class CommandsTests
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private long _now = 1_000;
    private int _exits;
    private readonly RecordingWindow _window = new();

    private Commands Create(string? commandFile = null) =>
        new(_window, commandFile, "Starting...", WindowTimeout, Lifetime, () => _now, () => _exits++);

    private static string Text(string text) => "TEXT " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static void Send(Commands commands, params string[] lines)
    {
        foreach (var line in lines)
        {
            commands.Apply(line);
            commands.Update();
        }
    }

    [Fact]
    public void TheNewestTextOfTheActiveUsersIsShownUntilAllEnd()
    {
        var commands = Create();

        Send(commands, "a " + Text("Activating a..."), "b " + Text("Checking for updates..."));
        Assert.True(_window.IsOpen);
        Assert.Equal("Checking for updates...", _window.Text);

        // b ends: its text is no longer shown, a's is
        Send(commands, "b END");
        Assert.Equal("Activating a...", _window.Text);
        Send(commands, "a CLOSE");
        Assert.False(_window.IsOpen);
        Assert.Equal(1, _window.Opened);
    }

    [Fact]
    public void AUserWithoutTextShowsTheInitialText()
    {
        var commands = Create();

        Send(commands, "a HEARTBEAT");

        Assert.True(_window.IsOpen);
        Assert.Equal("Starting...", _window.Text);
    }

    [Fact]
    public void AUserSilentForTheWindowTimeoutNoLongerKeepsTheWindowOpen()
    {
        var commands = Create();
        Send(commands, "a " + Text("Working..."));

        _now += (long)WindowTimeout.TotalMilliseconds - 1;
        commands.Update();
        Assert.True(_window.IsOpen);
        _now += 1;
        commands.Update();
        Assert.False(_window.IsOpen);

        // a heartbeat brings it back
        Send(commands, "a HEARTBEAT");
        Assert.True(_window.IsOpen);
        Assert.Equal("Working...", _window.Text);
    }

    [Fact]
    public void HoldKeepsAUserActiveUntilItsNextMessage()
    {
        var commands = Create();
        Send(commands, "a " + Text("Restarting the editor..."), "a HOLD 300");

        _now += 299_000;
        commands.Update();
        Assert.True(_window.IsOpen);

        // the next message restores the normal timeout
        Send(commands, "a HEARTBEAT");
        _now += (long)WindowTimeout.TotalMilliseconds;
        commands.Update();
        Assert.False(_window.IsOpen);
    }

    [Fact]
    public void LinesWithoutAUserBelongToOneAnonymousUser()
    {
        var commands = Create();

        Send(commands, Text("Anonymous"), "a " + Text("Named"));
        Send(commands, "END");

        Assert.True(_window.IsOpen);
        Assert.Equal("Named", _window.Text);
    }

    [Fact]
    public void MalformedLinesAreIgnored()
    {
        var commands = Create();
        Send(commands, "a " + Text("Valid"));

        Send(commands, "", "a", "a TEXT not-base64!", "a TEXT", "a HOLD soon", "a UNKNOWN 1", "b");

        Assert.True(_window.IsOpen);
        Assert.Equal("Valid", _window.Text);
    }

    [Fact]
    public void AHelperWithoutCommandFileExitsOnceEveryoneWasSilentForItsLifetime()
    {
        var commands = Create();
        Send(commands, "a " + Text("Working..."), "a END");

        var closed = _window.Closed;
        _now += (long)Lifetime.TotalMilliseconds - 1;
        commands.Update();
        Assert.Equal(closed, _window.Closed);
        _now += 1;
        commands.Update();
        Assert.Equal(closed + 1, _window.Closed);
        // it exits on the UI thread, once the window is hidden, not on the thread that noticed the lifetime passed
        Assert.Equal(0, _exits);
        commands.Hidden();
        Assert.Equal(1, _exits);
    }

    [Fact]
    public void AHelperWithdrawsItsRecordBeforeExitingAndAMessageInBetweenRestoresIt()
    {
        var directory = Directory.CreateTempSubdirectory("eplugin-helper-");
        try
        {
            var commandFile = Path.Combine(directory.FullName, "token.commands");
            var record = Path.ChangeExtension(commandFile, ".helper");
            File.WriteAllText(commandFile, "");
            File.WriteAllText(record, "record");
            var commands = Create(commandFile);

            _now += (long)Lifetime.TotalMilliseconds;
            commands.Update();
            Assert.False(File.Exists(record));
            Assert.True(File.Exists(Path.ChangeExtension(commandFile, ".exiting")));

            // an editor connected just before the record was withdrawn
            Send(commands, "late HEARTBEAT");
            Assert.True(File.Exists(record));
            Assert.Equal(0, _exits);

            _now += (long)Lifetime.TotalMilliseconds;
            commands.Update();
            _now += 1_000;
            commands.Update();
            Assert.False(File.Exists(commandFile));
            Assert.Equal(0, _exits);
            commands.Hidden();
            Assert.Equal(1, _exits);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class RecordingWindow : IProgressWindow
    {
        public string? Text { get; private set; }
        public bool IsOpen { get; private set; }
        public int Opened { get; private set; }
        public int Closed { get; private set; }

        public void Run(string title, string text, int showDelayMilliseconds, Action ready, Action hidden) => ready();
        public void SetText(string text) => Text = text;

        public void Open()
        {
            IsOpen = true;
            Opened++;
        }

        public void Close()
        {
            IsOpen = false;
            Closed++;
        }
    }
}
