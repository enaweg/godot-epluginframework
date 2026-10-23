using System.Text;
using Xunit;

namespace ActivationProgress.Tests;

/// <summary>How the helper reads the command file that editors append to, also while a command is half written.</summary>
public sealed class CommandFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eplugin-commands-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private StreamReader Open() =>
        new(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.ASCII);

    private void Append(string text)
    {
        using var writer = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write(Encoding.ASCII.GetBytes(text));
    }

    [Fact]
    public async Task ALineIsReadOnceItsNewlineIsWrittenAlsoAcrossAppendsAndAfterTheEnd()
    {
        File.WriteAllText(_path, "HEARTBEAT a\r\nTEXT a ab");
        using var input = Open();
        var pending = new StringBuilder();

        // Windows line endings are trimmed
        Assert.Equal("HEARTBEAT a", Commands.ReadAppendedLine(input, pending));

        // the rest of a half-written command is waited for at the end of the file
        var next = Task.Run(() => Commands.ReadAppendedLine(input, pending));
        await Task.Delay(200);
        Assert.False(next.IsCompleted);
        Append("Yw==\nEND a\n");
        Assert.Equal("TEXT a abYw==", await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("END a", Commands.ReadAppendedLine(input, pending));
    }
}
