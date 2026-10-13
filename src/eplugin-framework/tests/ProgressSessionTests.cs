using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Enaweg.Plugin.Internal;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class ProgressSessionTests
{
    private static readonly ProcessIdentity Editor = new(1_000_000, 7);
    private static readonly ProcessIdentity OtherEditor = new(1_000_001, 7);
    private static readonly ProcessIdentity Helper = new(1_000_002, 7);
    private readonly HashSet<ProcessIdentity> _running = [];
    private string _root = null!;
    private int _started;

    [BeforeTest]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "eplugin-progress-" + Guid.NewGuid().ToString("N"));
        _running.Clear();
        _started = 0;
    }

    [AfterTest]
    public void Cleanup()
    {
        ProgressSession.IsRunning = ProcessIdentity.IsRunning;
        ProgressSession.Self = ProcessIdentity.Current();
        // A closed helper still plays its closing animation, and Windows keeps its runtime copy locked until it exits.
        for (var attempt = 0; Directory.Exists(_root); attempt++)
        {
            try { Directory.Delete(_root, true); }
            catch (Exception) when (attempt < 50) { System.Threading.Thread.Sleep(100); }
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void SameProcessSurvivesAddonReplacementHandoffsAndTheNextOperation()
    {
        var addon = Path.Combine(_root, "addon");
        Directory.CreateDirectory(addon);
        foreach (var name in new[] { "ActivationProgress.dll", "ActivationProgress.runtimeconfig.json" })
            File.Copy(ProjectSettings.GlobalizePath("res://addons/ePlugin/progress/" + name), Path.Combine(addon, name));
        var directory = Path.Combine(_root, "session");
        IProgressHelper? session = ProgressSession.Start(Path.Combine(addon, "ActivationProgress.dll"), directory, []);
        Assertions.AssertObject(session).IsNotNull();
        try
        {
            session!.Send("HEARTBEAT");
            session.DetachForRestart();
            session = null;
            var state = Directory.GetFiles(directory, "*.owned").Single();
            var original = File.ReadAllLines(state);
            // The running helper must no longer lock any file in the framework's addon directory.
            Directory.Delete(addon, true);
            for (var restart = 0; restart < 2; restart++)
            {
                var fields = File.ReadAllLines(state);
                // Linux reports slightly different start timestamps from different editor hosts.
                fields[1] = (long.Parse(fields[1], CultureInfo.InvariantCulture) + TimeSpan.TicksPerMillisecond * 10)
                    .ToString(CultureInfo.InvariantCulture);
                File.WriteAllLines(state, fields);
                session = ProgressSession.TryResume(directory);
                Assertions.AssertObject(session).IsNotNull();
                session!.Send("TEXT V29ya2luZy4uLg==");
                session.DetachForRestart();
                session = null;
                Assertions.AssertString(File.ReadAllLines(state)[0]).IsEqual(original[0]);
            }

            session = ProgressSession.TryResume(directory);
            Assertions.AssertObject(session).IsNotNull();
            session!.Dispose();
            // The next operation reopens the closing window instead of starting a second helper.
            session = ProgressSession.Acquire(directory, () => throw new InvalidOperationException("A second helper was started."));
            Assertions.AssertObject(session).IsNotNull();
            session!.Send("TEXT V29ya2luZy4uLg==");
            session.Dispose();
            session = null;
            // Unused, the helper exits on its own once its window is hidden, and removes its files.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Directory.GetFiles(directory).Length > 0 && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(100);
            Assertions.AssertThat(Directory.GetFiles(directory).Select(Path.GetFileName).ToArray()).IsEmpty();
        }
        finally
        {
            session?.Dispose();
        }
    }

    [TestCase]
    public void ClosedHelperIsReusedInsteadOfStartingAnother()
    {
        Simulate(Editor, Helper);
        var commands = WriteState("idle", Helper, OtherEditor, false);
        var session = ProgressSession.Acquire(_root, Start);
        Assertions.AssertObject(session).IsNotNull();
        Assertions.AssertInt(_started).IsEqual(0);
        Assertions.AssertBool(File.Exists(StatePath("idle"))).IsFalse();
        Assertions.AssertThat(File.ReadAllLines(StatePath("owned"))).ContainsExactly(Lines(Helper, Editor, false));
        session!.Send("TEXT V29ya2luZy4uLg==");
        session.Dispose();
        // Closed again, the helper can be reused by the next operation, or claim the file to exit.
        Assertions.AssertBool(File.Exists(StatePath("owned"))).IsFalse();
        Assertions.AssertBool(File.Exists(StatePath("idle"))).IsTrue();
        Assertions.AssertThat(File.ReadAllLines(commands)).ContainsExactly("HEARTBEAT", "TEXT V29ya2luZy4uLg==", "CLOSE");
    }

    [TestCase]
    public void HelperLostToAnAssemblyReloadIsTakenOver()
    {
        Simulate(Editor, Helper);
        WriteState("owned", Helper, Editor, false);
        Assertions.AssertObject(ProgressSession.Acquire(_root, Start)).IsNotNull();
        Assertions.AssertInt(_started).IsEqual(0);
    }

    [TestCase]
    public void RestartHandoffThatNeverRestartedIsTakenOverAfterAReload()
    {
        Simulate(Editor, Helper);
        WriteState("owned", Helper, Editor, true);
        Assertions.AssertObject(ProgressSession.Acquire(_root, Start)).IsNotNull();
        Assertions.AssertInt(_started).IsEqual(0);
        Assertions.AssertThat(File.ReadAllLines(StatePath("owned"))).ContainsExactly(Lines(Helper, Editor, false));
    }

    [TestCase]
    public void HelperOfAnEditorThatEndedIsTakenOver()
    {
        Simulate(Editor, Helper);
        WriteState("owned", Helper, OtherEditor, false);
        Assertions.AssertObject(ProgressSession.Acquire(_root, Start)).IsNotNull();
        Assertions.AssertInt(_started).IsEqual(0);
        Assertions.AssertThat(File.ReadAllLines(StatePath("owned"))).ContainsExactly(Lines(Helper, Editor, false));
    }

    [TestCase]
    public void WindowOfAnotherRunningEditorIsLeftAlone()
    {
        Simulate(Editor, OtherEditor, Helper);
        var commands = WriteState("owned", Helper, OtherEditor, false);
        Assertions.AssertObject(ProgressSession.Acquire(_root, Start)).IsNull();
        Assertions.AssertInt(_started).IsEqual(0);
        Assertions.AssertThat(File.ReadAllLines(StatePath("owned"))).ContainsExactly(Lines(Helper, OtherEditor, false));
        Assertions.AssertString(File.ReadAllText(commands)).IsEmpty();
    }

    [TestCase]
    public void ExitedHelperIsDiscardedAndAnotherStarted()
    {
        Simulate(Editor);
        WriteState("idle", Helper, Editor, false);
        WriteState("owned", new ProcessIdentity(1_000_011, 7), Editor, false, "other");
        Assertions.AssertObject(ProgressSession.Acquire(_root, Start)).IsNull();
        Assertions.AssertInt(_started).IsEqual(1);
        Assertions.AssertThat(Directory.GetFiles(_root)).IsEmpty();
    }

    [TestCase]
    public void MalformedStateIsIgnored()
    {
        Simulate(Editor, Helper);
        Directory.CreateDirectory(_root);
        File.WriteAllText(StatePath("owned"), "invalid");
        File.WriteAllText(StatePath("commands"), "");
        Assertions.AssertObject(ProgressSession.Acquire(_root, Start)).IsNull();
        Assertions.AssertInt(_started).IsEqual(1);
        Assertions.AssertObject(ProgressSession.TryResume(_root)).IsNull();
    }

    [TestCase]
    public void StartupResumesTheRestartHandoffAndClosesAbandonedWindows()
    {
        Simulate(Editor, OtherEditor, Helper);
        var abandoned = new ProcessIdentity(1_000_011, 7);
        _running.Add(abandoned);
        // The editor that restarted may still be shutting down.
        var handedOver = WriteState("owned", Helper, OtherEditor, true);
        var crashed = WriteState("owned", abandoned, new ProcessIdentity(1_000_010, 7), false, "crashed");
        var session = ProgressSession.TryResume(_root);
        Assertions.AssertObject(session).IsNotNull();
        Assertions.AssertThat(File.ReadAllLines(StatePath("owned"))).ContainsExactly(Lines(Helper, Editor, false));
        Assertions.AssertThat(File.ReadAllLines(handedOver)).ContainsExactly("HEARTBEAT");
        Assertions.AssertThat(File.ReadAllLines(crashed)).ContainsExactly("HEARTBEAT", "CLOSE");
        Assertions.AssertBool(File.Exists(StatePath("idle", "crashed"))).IsTrue();
        session!.Dispose();
    }

    [TestCase]
    public void RestartMarkerOfEarlierVersionsIsResumed()
    {
        Simulate(Editor, Helper);
        Directory.CreateDirectory(_root);
        File.WriteAllText(StatePath("commands"), "");
        File.WriteAllLines(Path.Combine(_root, "restart"),
            [Token(), Helper.Id.ToString(CultureInfo.InvariantCulture), Helper.StartUtcTicks.ToString(CultureInfo.InvariantCulture)]);
        Assertions.AssertObject(ProgressSession.TryResume(_root)).IsNotNull();
        Assertions.AssertBool(File.Exists(Path.Combine(_root, "restart"))).IsFalse();
        Assertions.AssertThat(File.ReadAllLines(StatePath("owned"))).ContainsExactly(Lines(Helper, Editor, false));
    }

    [TestCase]
    public void StaleMarkerCannotAttachToAnUnrelatedProcess()
    {
        Directory.CreateDirectory(_root);
        using var process = Process.GetCurrentProcess();
        File.WriteAllText(StatePath("commands"), "");
        File.WriteAllLines(Path.Combine(_root, "restart"),
        [
            Token(), process.Id.ToString(CultureInfo.InvariantCulture),
            (process.StartTime.ToUniversalTime().Ticks - TimeSpan.TicksPerMinute).ToString(CultureInfo.InvariantCulture)
        ]);
        Assertions.AssertObject(ProgressSession.TryResume(_root)).IsNull();
        Assertions.AssertThat(Directory.GetFiles(_root, "*.owned")).IsEmpty();
    }

    private void Simulate(ProcessIdentity self, params ProcessIdentity[] running)
    {
        ProgressSession.Self = self;
        _running.Add(self);
        foreach (var identity in running) _running.Add(identity);
        ProgressSession.IsRunning = _running.Contains;
    }

    private IProgressHelper? Start()
    {
        _started++;
        return null;
    }

    /// <summary>A helper's token (32 hex digits), derived from a readable name.</summary>
    private static string Token(string name = "helper") =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();

    private string StatePath(string extension, string name = "helper") => Path.Combine(_root, Token(name) + "." + extension);

    /// <summary>Writes a helper's state file and an empty command file, and returns the command file.</summary>
    private string WriteState(string extension, ProcessIdentity helper, ProcessIdentity owner, bool restart, string name = "helper")
    {
        Directory.CreateDirectory(_root);
        File.WriteAllLines(StatePath(extension, name), Lines(helper, owner, restart));
        var commands = StatePath("commands", name);
        File.WriteAllText(commands, "");
        return commands;
    }

    private static string[] Lines(ProcessIdentity helper, ProcessIdentity owner, bool restart) =>
    [
        helper.Id.ToString(CultureInfo.InvariantCulture), helper.StartUtcTicks.ToString(CultureInfo.InvariantCulture),
        owner.Id.ToString(CultureInfo.InvariantCulture), owner.StartUtcTicks.ToString(CultureInfo.InvariantCulture),
        restart ? "1" : "0"
    ];
}
