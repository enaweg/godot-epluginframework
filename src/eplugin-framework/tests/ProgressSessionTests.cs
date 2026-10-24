#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class ProgressSessionTests
{
    private const string User = "0123456789abcdef0123456789abcdef";
    private readonly HashSet<ProcessIdentity> _running = [];
    private string _root = null!;
    private string _shared = null!;
    private string _handoff = null!;
    private int _started;

    [BeforeTest]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "eplugin-progress-" + Guid.NewGuid().ToString("N"));
        _shared = Path.Combine(_root, "shared");
        _handoff = Path.Combine(_root, "project", "handoff");
        Directory.CreateDirectory(_shared);
        Directory.CreateDirectory(Path.GetDirectoryName(_handoff)!);
        _running.Clear();
        _started = 0;
    }

    [AfterTest]
    public void Cleanup()
    {
        ProgressSession.IsRunning = ProcessIdentity.IsRunning;
        // Windows keeps the helper's runtime copy locked until it exits.
        for (var attempt = 0; Directory.Exists(_root); attempt++)
        {
            try { Directory.Delete(_root, true); }
            catch (Exception) when (attempt < 50) { System.Threading.Thread.Sleep(100); }
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void EditorsShareOneHelperThatOutlivesRestartsAndExitsAfterItsLifetime()
    {
        var addon = Path.Combine(_root, "addon");
        Directory.CreateDirectory(addon);
        foreach (var name in new[] { "ActivationProgress.dll", "ActivationProgress.runtimeconfig.json" })
            File.Copy(ProjectSettings.GlobalizePath("res://addons/ePlugin/progress/" + name), Path.Combine(addon, name));
        var session = ProgressSession.Start(Path.Combine(addon, "ActivationProgress.dll"), _shared, _handoff,
            ["--max-lifetime-seconds", "3"]);
        Assertions.AssertObject(session).IsNotNull();
        session!.Send(User, "TEXT V29ya2luZy4uLg==");
        // Another editor connects to the running helper instead of starting a second one.
        using (var other = ProgressSession.Acquire(_shared, _handoff, () => throw new InvalidOperationException("A second helper was started.")))
        {
            other!.Send("fedcba9876543210fedcba9876543210", "TEXT Q2hlY2tpbmcuLi4=");
            other.Send("fedcba9876543210fedcba9876543210", "END");
        }

        session.Send(User, "HOLD 300");
        session.HandOver(User);
        session.Dispose();
        // The running helper must no longer lock any file in the framework's addon directory.
        Directory.Delete(addon, true);
        var resumed = ProgressSession.TryResume(_handoff, _shared);
        Assertions.AssertObject(resumed).IsNotNull();
        Assertions.AssertString(resumed!.Value.User).IsEqual(User);
        Assertions.AssertBool(File.Exists(_handoff)).IsFalse();
        resumed.Value.Session!.Send(User, "END");
        resumed.Value.Session.Dispose();

        // Its lifetime counts from the last message; then it withdraws its record and removes its files.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Directory.GetFiles(_shared).Length > 0 && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(100);
        Assertions.AssertThat(Directory.GetFiles(_shared).Select(Path.GetFileName).ToArray()).IsEmpty();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ARuntimeCopyCutOffByAKilledEditorIsReplaced()
    {
        var addon = Path.Combine(_root, "addon");
        Directory.CreateDirectory(addon);
        foreach (var name in new[] { "ActivationProgress.dll", "ActivationProgress.runtimeconfig.json" })
            File.Copy(ProjectSettings.GlobalizePath("res://addons/ePlugin/progress/" + name), Path.Combine(addon, name));
        var helper = Path.Combine(addon, "ActivationProgress.dll");
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(File.ReadAllBytes(helper));
        hash.AppendData(File.ReadAllBytes(Path.Combine(addon, "ActivationProgress.runtimeconfig.json")));
        var runtime = Path.Combine(_shared, "runtime", Convert.ToHexString(hash.GetHashAndReset()));
        Directory.CreateDirectory(runtime);
        File.WriteAllBytes(Path.Combine(runtime, "ActivationProgress.dll"), File.ReadAllBytes(helper)[..100]);

        using (var session = ProgressSession.Start(helper, _shared, _handoff, ["--max-lifetime-seconds", "1"]))
        {
            Assertions.AssertObject(session).IsNotNull();
            session!.Send(User, "END");
        }

        Assertions.AssertThat(File.ReadAllBytes(Path.Combine(runtime, "ActivationProgress.dll"))).IsEqual(File.ReadAllBytes(helper));
        Assertions.AssertThat(Directory.GetFiles(runtime, "*.partial")).IsEmpty();
    }

    [TestCase]
    public void EditorsConnectToTheOldestRunningHelperOfTheirProtocol()
    {
        var oldest = Helper("oldest", new ProcessIdentity(1_000_001, 10));
        Helper("newer", new ProcessIdentity(1_000_002, 20));
        Helper("other-protocol", new ProcessIdentity(1_000_003, 5), protocol: 1);
        Helper("exited", new ProcessIdentity(1_000_004, 1), running: false);
        using (var session = ProgressSession.Acquire(_shared, _handoff, Start))
        {
            session!.Send(User, "HEARTBEAT");
        }

        Assertions.AssertInt(_started).IsEqual(0);
        Assertions.AssertThat(File.ReadAllLines(oldest)).ContainsExactly(User + " HEARTBEAT");
        // A helper of another framework version is left alone; the files of one that exited are removed.
        Assertions.AssertBool(File.Exists(PathOf("other-protocol", ".helper"))).IsTrue();
        Assertions.AssertBool(File.Exists(PathOf("exited", ".helper"))).IsFalse();
        Assertions.AssertBool(File.Exists(PathOf("exited", ".commands"))).IsFalse();
    }

    [TestCase]
    public void HelperIsStartedWhenNoneRuns()
    {
        Helper("exited", new ProcessIdentity(1_000_004, 1), running: false);
        File.WriteAllText(PathOf("malformed", ".helper"), "invalid");
        Assertions.AssertObject(ProgressSession.Acquire(_shared, _handoff, Start)).IsNull();
        Assertions.AssertInt(_started).IsEqual(1);
    }

    [TestCase]
    public void SendFailsOnceTheHelperExited()
    {
        var helper = new ProcessIdentity(1_000_001, 10);
        Helper("helper", helper);
        using var session = ProgressSession.Acquire(_shared, _handoff, Start);
        _running.Remove(helper);
        Assertions.AssertThrown(() => session!.Send(User, "HEARTBEAT")).IsInstanceOf<IOException>();
    }

    [TestCase]
    public void EditorsAppendWithoutOverwritingEachOthersCommands()
    {
        var commands = Helper("helper", new ProcessIdentity(1_000_001, 10));
        using (var first = ProgressSession.Acquire(_shared, _handoff, Start))
        using (var second = ProgressSession.Acquire(_shared, _handoff, Start))
        {
            first!.Send(User, "TEXT QQ==");
            second!.Send(User, "TEXT Qg==");
            first.Send(User, "END");
        }

        Assertions.AssertThat(File.ReadAllLines(commands))
            .ContainsExactly(User + " TEXT QQ==", User + " TEXT Qg==", User + " END");
    }

    [TestCase]
    public void InaccessibleHelperDoesNotPreventConnectingToAnotherOne()
    {
        var blocked = Helper("oldest", new ProcessIdentity(1_000_001, 10));
        using var locked = new FileStream(blocked, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None);
        var commands = Helper("newer", new ProcessIdentity(1_000_002, 20));
        using (var session = ProgressSession.Acquire(_shared, _handoff, Start))
        {
            session!.Send(User, "HEARTBEAT");
        }

        Assertions.AssertInt(_started).IsEqual(0);
        Assertions.AssertThat(File.ReadAllLines(commands)).ContainsExactly(User + " HEARTBEAT");
    }

    [TestCase]
    public static void WritersOfEveryTerminalSessionShareTheMutex()
    {
        // Without the prefix, .NET scopes a named mutex to the terminal session on Linux and macOS.
        Assertions.AssertString(ProgressSession.MutexName("token")).IsEqual(@"Global\eplugin-progress-token");
        using var mutex = new System.Threading.Mutex(false, ProgressSession.MutexName(Guid.NewGuid().ToString("N")));
        Assertions.AssertBool(mutex.WaitOne(0)).IsTrue();
        mutex.ReleaseMutex();
    }

    [TestCase]
    public async Task ConcurrentWritersKeepEveryCompleteCommand()
    {
        var commands = Helper("helper", new ProcessIdentity(1_000_001, 10));
        var sessions = Enumerable.Range(0, 4).Select(_ => ProgressSession.Acquire(_shared, _handoff, Start)!).ToArray();
        var expected = Enumerable.Range(0, 4).SelectMany(writer => Enumerable.Range(0, 50)
            .Select(index => writer + " TEXT " + index + new string('A', 16_384))).ToArray();
        try
        {
            await Task.WhenAll(sessions.Select((session, writer) => Task.Run(() =>
            {
                for (var index = 0; index < 50; index++)
                    session.Send(writer.ToString(CultureInfo.InvariantCulture), "TEXT " + index + new string('A', 16_384));
            })));
        }
        finally
        {
            foreach (var session in sessions) session.Dispose();
        }

        Assertions.AssertThat(File.ReadAllLines(commands).OrderBy(line => line, StringComparer.Ordinal).ToArray())
            .ContainsExactly(expected.OrderBy(line => line, StringComparer.Ordinal).ToArray());
    }

    [TestCase]
    public void RestartedEditorContinuesTheHandedOverUserOnce()
    {
        var commands = Helper("helper", new ProcessIdentity(1_000_001, 10));
        using (var session = ProgressSession.Acquire(_shared, _handoff, Start))
        {
            session!.HandOver(User);
        }

        var resumed = ProgressSession.TryResume(_handoff, _shared);
        Assertions.AssertObject(resumed).IsNotNull();
        Assertions.AssertString(resumed!.Value.User).IsEqual(User);
        resumed.Value.Session!.Send(User, "HEARTBEAT");
        resumed.Value.Session.Dispose();
        Assertions.AssertThat(File.ReadAllLines(commands)).ContainsExactly(User + " HEARTBEAT");
        Assertions.AssertObject(ProgressSession.TryResume(_handoff, _shared)).IsNull();
    }

    [TestCase]
    public void UserHandedOverByAnEarlierProtocolIsEndedInItsHelper()
    {
        // A self-update from 0.11.0 (protocol 3) restarts into this protocol: its helper would hold the window for minutes.
        var helper = new ProcessIdentity(1_000_001, 10);
        var commands = Helper("earlier", helper, protocol: 3);
        File.WriteAllLines(_handoff, ["3", Token("earlier"), .. helper.Fields, User]);
        var resumed = ProgressSession.TryResume(_handoff, _shared);
        Assertions.AssertObject(resumed).IsNotNull();
        Assertions.AssertObject(resumed!.Value.Session).IsNull();
        Assertions.AssertString(resumed.Value.User).IsEqual(User);
        Assertions.AssertThat(File.ReadAllLines(commands)).ContainsExactly(User + " END");
        Assertions.AssertBool(File.Exists(_handoff)).IsFalse();

        // Protocols before users and handoffs are left alone, as is the handoff of a helper that exited.
        File.WriteAllLines(_handoff, ["2", Token("earlier"), .. helper.Fields, User]);
        Assertions.AssertObject(ProgressSession.TryResume(_handoff, _shared)).IsNull();
        _running.Remove(helper);
        File.WriteAllLines(_handoff, ["3", Token("earlier"), .. helper.Fields, User]);
        Assertions.AssertObject(ProgressSession.TryResume(_handoff, _shared)).IsNull();
        Assertions.AssertThat(File.ReadAllLines(commands)).ContainsExactly(User + " END");
    }

    [TestCase]
    public void HandoffOfAHelperThatExitedIsDropped()
    {
        var helper = new ProcessIdentity(1_000_001, 10);
        Helper("helper", helper);
        using (var session = ProgressSession.Acquire(_shared, _handoff, Start))
        {
            session!.HandOver(User);
        }

        _running.Remove(helper);
        Assertions.AssertObject(ProgressSession.TryResume(_handoff, _shared)).IsNull();
        Assertions.AssertBool(File.Exists(_handoff)).IsFalse();
        File.WriteAllText(_handoff, "invalid");
        Assertions.AssertObject(ProgressSession.TryResume(_handoff, _shared)).IsNull();
    }

    [TestCase]
    public void HelperOfAnEarlierVersionIsClosedOnStartup()
    {
        var project = Path.GetDirectoryName(_handoff)!;
        var token = Guid.NewGuid().ToString("N");
        var commands = Path.Combine(project, token + ".commands");
        File.WriteAllText(commands, "");
        File.WriteAllLines(Path.Combine(project, "restart"), [token, "1000001", "10"]);
        Assertions.AssertObject(ProgressSession.TryResume(_handoff, _shared)).IsNull();
        Assertions.AssertThat(File.ReadAllLines(commands)).ContainsExactly("CLOSE");
        Assertions.AssertBool(File.Exists(Path.Combine(project, "restart"))).IsFalse();
    }

    private IProgressHelper? Start()
    {
        _started++;
        return null;
    }

    private string PathOf(string name, string extension) => Path.Combine(_shared, Token(name) + extension);

    private static string Token(string name) =>
        // Handoffs require a token in the GUID's 32-character N format.
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..32].ToLowerInvariant();

    /// <summary>Publishes a simulated helper and returns its command file.</summary>
    private string Helper(string name, ProcessIdentity helper, int protocol = ProgressSession.Protocol, bool running = true)
    {
        if (running) _running.Add(helper);
        ProgressSession.IsRunning = _running.Contains;
        File.WriteAllLines(PathOf(name, ".helper"),
            [protocol.ToString(CultureInfo.InvariantCulture), helper.Id.ToString(CultureInfo.InvariantCulture), helper.StartUtcTicks.ToString(CultureInfo.InvariantCulture)]);
        var commands = PathOf(name, ".commands");
        File.WriteAllText(commands, "");
        return commands;
    }
}
#endif
