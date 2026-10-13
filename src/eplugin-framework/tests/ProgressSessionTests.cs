using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Enaweg.Plugin.Internal;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class ProgressSessionTests
{
    private string _root = null!;

    [BeforeTest]
    public void Setup() => _root = Path.Combine(Path.GetTempPath(), "eplugin-progress-" + Guid.NewGuid().ToString("N"));

    [AfterTest]
    public void Cleanup()
    {
        // A closed helper still plays its closing animation, and Windows keeps its runtime copy locked until it exits.
        for (var attempt = 0; Directory.Exists(_root); attempt++)
        {
            try { Directory.Delete(_root, true); }
            catch (Exception) when (attempt < 50) { System.Threading.Thread.Sleep(100); }
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void SameProcessSurvivesAddonReplacementAndTwoHandoffs()
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
            var marker = Path.Combine(directory, "restart");
            var original = File.ReadAllLines(marker);
            // The running helper must no longer lock any file in the framework's addon directory.
            Directory.Delete(addon, true);
            for (var restart = 0; restart < 2; restart++)
            {
                var fields = File.ReadAllLines(marker);
                // Linux reports slightly different start timestamps from different editor hosts.
                fields[2] = (long.Parse(fields[2], CultureInfo.InvariantCulture) + TimeSpan.TicksPerMillisecond * 10)
                    .ToString(CultureInfo.InvariantCulture);
                File.WriteAllLines(marker, fields);
                session = ProgressSession.TryResume(directory);
                Assertions.AssertObject(session).IsNotNull();
                session!.Send("TEXT V29ya2luZy4uLg==");
                session.DetachForRestart();
                session = null;
                Assertions.AssertString(File.ReadAllLines(marker)[1]).IsEqual(original[1]);
            }
            session = ProgressSession.TryResume(directory);
            Assertions.AssertObject(session).IsNotNull();
            session!.Dispose();
            session = null;
            Assertions.AssertBool(File.Exists(marker)).IsFalse();
            Assertions.AssertInt(Directory.GetFiles(directory, "*.commands").Length).IsEqual(0);
        }
        finally
        {
            session?.Dispose();
            ProgressSession.TryResume(directory)?.Dispose();
        }
    }

    [TestCase]
    public void StaleMarkerCannotAttachToAnUnrelatedProcess()
    {
        Directory.CreateDirectory(_root);
        using var process = Process.GetCurrentProcess();
        var token = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(_root, token + ".commands"), "");
        File.WriteAllLines(Path.Combine(_root, "restart"),
        [
            token, process.Id.ToString(CultureInfo.InvariantCulture),
            (process.StartTime.ToUniversalTime().Ticks - TimeSpan.TicksPerMinute).ToString(CultureInfo.InvariantCulture)
        ]);
        Assertions.AssertObject(ProgressSession.TryResume(_root)).IsNull();
    }

    [TestCase]
    public void ExpiredAndMalformedMarkersAreIgnored()
    {
        Directory.CreateDirectory(_root);
        var marker = Path.Combine(_root, "restart");
        File.WriteAllText(marker, "invalid");
        Assertions.AssertObject(ProgressSession.TryResume(_root)).IsNull();
        using var process = Process.GetCurrentProcess();
        File.WriteAllLines(marker,
        [
            Guid.NewGuid().ToString("N"), process.Id.ToString(CultureInfo.InvariantCulture),
            process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)
        ]);
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow - TimeSpan.FromHours(1));
        Assertions.AssertObject(ProgressSession.TryResume(_root)).IsNull();
    }
}
