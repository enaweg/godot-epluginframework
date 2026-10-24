#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enaweg.Plugin.Internal.Dotnet;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>
/// What <see cref="BuildOutputGuard"/> does when the build throws or the file system does not cooperate; the usual
/// failed and successful builds are covered through the dotnet CLIs in <see cref="IDotnetCliTests"/>.
/// </summary>
[TestSuite]
public class BuildOutputGuardTests
{
    private string _root = null!;
    private string _output = null!;
    private string _snapshot = null!;
    private readonly List<string> _log = [];

    [BeforeTest]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "eplugin-guard-" + Guid.NewGuid().ToString("N"));
        _output = Path.Combine(_root, "bin");
        _snapshot = Path.Combine(_root, "build-output");
        Directory.CreateDirectory(_output);
        File.WriteAllText(Path.Combine(_output, "Game.dll"), "old");
        _log.Clear();
    }

    [AfterTest]
    public void Cleanup() => Directory.Delete(_root, true);

    private BuildOutcome Run(Func<BuildOutcome> build) =>
        BuildOutputGuard.Run(_output, _snapshot, "Game.dll", new RecordingLogger(_log, "guard"), build);

    private void Clean()
    {
        Directory.Delete(_output, true);
        Directory.CreateDirectory(_output);
    }

    [TestCase]
    public void ABuildThatThrowsAfterCleaningStillGetsTheAssemblyBack()
    {
        Assertions.AssertThrown(() => Run(() =>
        {
            Clean();
            throw new InvalidOperationException("dotnet crashed");
        })).IsInstanceOf<InvalidOperationException>();

        Assertions.AssertString(File.ReadAllText(Path.Combine(_output, "Game.dll"))).IsEqual("old");
    }

    [TestCase]
    public void ASnapshotThatCannotBeTakenIsLoggedAndTheBuildStillRuns()
    {
        // a file where the snapshot folder belongs
        File.WriteAllText(_snapshot, "in the way");
        var built = false;

        var outcome = Run(() =>
        {
            built = true;
            return new BuildOutcome(0, []);
        });

        Assertions.AssertBool(built).IsTrue();
        Assertions.AssertInt(outcome.ExitCode).IsEqual(0);
        Assertions.AssertBool(_log.Any(l => l.StartsWith("warn:", StringComparison.Ordinal) && l.Contains("Cannot keep a copy"))).IsTrue();
    }

    [TestCase]
    public void AnAssemblyCompiledByAFailedBuildStaysUnlessItsCodeIsRemovedAgain()
    {
        var old = File.GetLastWriteTimeUtc(Path.Combine(_output, "Game.dll"));
        BuildOutcome CompiledThenFailed()
        {
            // the project compiled, another project of the solution did not
            Clean();
            File.WriteAllText(Path.Combine(_output, "Game.dll"), "new");
            return new(1, ["error in Other.csproj"]);
        }

        Assertions.AssertInt(Run(CompiledThenFailed).ExitCode).IsEqual(1);
        Assertions.AssertString(File.ReadAllText(Path.Combine(_output, "Game.dll"))).IsEqual("new");

        File.WriteAllText(Path.Combine(_output, "Game.dll"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(_output, "Game.dll"), old);
        var outcome = BuildOutputGuard.Run(_output, _snapshot, "Game.dll", new RecordingLogger(_log, "guard"), CompiledThenFailed, keepNewAssembly: false);
        Assertions.AssertInt(outcome.ExitCode).IsEqual(1);
        Assertions.AssertString(File.ReadAllText(Path.Combine(_output, "Game.dll"))).IsEqual("old");
        // with its timestamp, so the editor does not take it for a new build
        Assertions.AssertObject(File.GetLastWriteTimeUtc(Path.Combine(_output, "Game.dll"))).IsEqual(old);
    }

    [TestCase]
    public void AnAssemblyThatCannotBePutBackIsReportedAndTheFailureReturned()
    {
        var outcome = Run(() =>
        {
            Clean();
            // the snapshot is lost while the build runs
            Directory.Delete(_snapshot, true);
            File.WriteAllText(_snapshot, "not a folder");
            return new BuildOutcome(1, ["error"]);
        });

        Assertions.AssertInt(outcome.ExitCode).IsEqual(1);
        Assertions.AssertBool(File.Exists(Path.Combine(_output, "Game.dll"))).IsFalse();
        Assertions.AssertBool(_log.Any(l => l.StartsWith("error:", StringComparison.Ordinal) && l.Contains("could not be put back"))).IsTrue();
    }
}
#endif
