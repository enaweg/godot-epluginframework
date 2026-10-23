#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "_timer is a fake heartbeat; the tests assert when the code under test disposes it.")]
public class ActivationProgressTests
{
    private readonly List<FakeHelper> _launched = [];
    private Action? _tick;
    private FakeDisposable? _timer;

    [BeforeTest]
    public void Setup()
    {
        ActivationProgress.Reset();
        _launched.Clear();
        _tick = null;
        _timer = null;
        ActivationProgress.HideSettingsDialog = () => null;
        ActivationProgress.IsEditorThread = () => true;
        ActivationProgress.Resume = () => null;
        ActivationProgress.StartHeartbeat = tick =>
        {
            _tick = tick;
            return _timer = new FakeDisposable();
        };
        ActivationProgress.Launch = () =>
        {
            var helper = new FakeHelper();
            _launched.Add(helper);
            return helper;
        };
    }

    [AfterTest]
    public static void Cleanup()
    {
        ActivationProgress.Reset();
        ActivationProgress.Launch = ActivationProgress.LaunchDefault;
        ActivationProgress.Resume = ActivationProgress.ResumeDefault;
        ActivationProgress.HideSettingsDialog = ProjectSettingsDialog.TryHide;
        ActivationProgress.IsEditorThread = () => Godot.OS.GetThreadCallerId() == Godot.OS.GetMainThreadId();
        ActivationProgress.StartHeartbeat = tick => new System.Threading.Timer(_ => tick(), null,
            ActivationProgress.HeartbeatInterval, ActivationProgress.HeartbeatInterval);
    }

    [TestCase]
    public async Task PublicScopesReportThroughIProgressAndIgnoreReportsAfterDisposal()
    {
        using (var outer = EPlugin.Progress.Begin("Importing assets..."))
        {
            var inner = EPlugin.Progress.Begin("Reading files...");
            IProgress<string> report = inner;
            await Task.Run(() => report.Report("Reading files... 50%"));
            inner.Dispose();
            inner.Dispose();
            await Task.Run(() => report.Report("Ignored after disposal."));
            outer.Report("Finishing...");
            Assertions.AssertBool(_launched[0].Disposed).IsFalse();
        }

        var helper = _launched.Single();
        Assertions.AssertThat(helper.Users).HasSize(1);
        Assertions.AssertThat(helper.Of(helper.Users[0]))
            .ContainsExactly("TEXT Importing assets...", "TEXT Reading files...", "TEXT Reading files... 50%",
                "TEXT Finishing...", "END");
        Assertions.AssertBool(helper.Disposed).IsTrue();
        Assertions.AssertBool(_timer!.Disposed).IsTrue();
    }

    [TestCase]
    public async Task PublicConcurrentOperationsKeepTheirOwnUsersAndHeartbeats()
    {
        var check = new TaskCompletionSource();
        var import = new TaskCompletionSource();
        var checking = PublicOperation("Checking assets...", check.Task, "Checked.");
        var importing = PublicOperation("Importing assets...", import.Task, "Imported.");
        _tick!();
        check.SetResult();
        await checking;
        Assertions.AssertBool(_launched[0].Disposed).IsFalse();
        import.SetResult();
        await importing;

        var helper = _launched.Single();
        Assertions.AssertThat(helper.Users).HasSize(2);
        Assertions.AssertThat(helper.Of(helper.Users[0]))
            .ContainsExactly("TEXT Checking assets...", "HEARTBEAT", "TEXT Checked.", "END");
        Assertions.AssertThat(helper.Of(helper.Users[1]))
            .ContainsExactly("TEXT Importing assets...", "HEARTBEAT", "TEXT Imported.", "END");
        Assertions.AssertBool(helper.Disposed).IsTrue();
    }

    private static async Task PublicOperation(string text, Task work, string done)
    {
        using var progress = EPlugin.Progress.Begin(text);
        await work;
        await Task.Run(() => progress.Report(done));
    }

    [TestCase]
    public void PublicProgressLeavesProjectSettingsVisible()
    {
        var hideCount = 0;
        ActivationProgress.HideSettingsDialog = () =>
        {
            hideCount++;
            return new FakeDisposable();
        };
        using (EPlugin.Progress.Begin("Importing assets..."))
        {
            using (EPlugin.Progress.Begin("Reading files...")) { }
        }

        Assertions.AssertInt(hideCount).IsEqual(0);
        Assertions.AssertBool(_launched.Single().Disposed).IsTrue();
    }

    [TestCase]
    public void LifecycleWorkInsidePublicProgressStillHidesAndRestoresSettings()
    {
        var dialog = new FakeDisposable();
        var hideCount = 0;
        ActivationProgress.HideSettingsDialog = () =>
        {
            hideCount++;
            return dialog;
        };
        using (EPlugin.Progress.Begin("Importing assets..."))
        {
            Assertions.AssertInt(hideCount).IsEqual(0);
            using (ActivationProgress.Begin("Activating plugin..."))
            {
                using (ActivationProgress.Begin("Activating dependency...")) { }
                Assertions.AssertInt(hideCount).IsEqual(1);
            }

            Assertions.AssertBool(dialog.Disposed).IsFalse();
        }

        Assertions.AssertBool(dialog.Disposed).IsTrue();
        Assertions.AssertThat(_launched.Single().Users).HasSize(1);
        // The next lifetime can hide settings again.
        using (ActivationProgress.Begin("Deactivating plugin...")) { }
        Assertions.AssertInt(hideCount).IsEqual(2);
    }

    [TestCase]
    public static void PublicProgressInsideLifecycleWorkDoesNotReopenSettingsEarly()
    {
        var dialog = new FakeDisposable();
        var hideCount = 0;
        ActivationProgress.HideSettingsDialog = () =>
        {
            hideCount++;
            return dialog;
        };
        using (ActivationProgress.Begin("Activating plugin..."))
        {
            using (EPlugin.Progress.Begin("Reading assets...")) { }
            Assertions.AssertInt(hideCount).IsEqual(1);
            Assertions.AssertBool(dialog.Disposed).IsFalse();
        }

        Assertions.AssertBool(dialog.Disposed).IsTrue();
    }

    [TestCase]
    public void PublicProgressRemainsUsableWithoutAHelper()
    {
        ActivationProgress.Launch = () => null;
        using (var progress = EPlugin.Progress.Begin("Importing assets..."))
        {
            Assertions.AssertObject(progress).IsNotNull();
            progress.Report("Reading files...");
            _tick!();
        }

        ActivationProgress.Launch = () => throw new IOException("No helper available.");
        using (var progress = EPlugin.Progress.Begin("Importing assets..."))
        {
            progress.Report("Reading files...");
            _tick!();
        }

        Assertions.AssertBool(_timer!.Disposed).IsTrue();
    }

    [TestCase]
    public async Task PublicScopeEndsOnCancellationAndExceptions()
    {
        try
        {
            using var progress = EPlugin.Progress.Begin("Importing assets...");
            throw new InvalidOperationException("Import failed.");
        }
        catch (InvalidOperationException) { }
        Assertions.AssertBool(_launched[0].Disposed).IsTrue();

        using var cancellation = new System.Threading.CancellationTokenSource();
        try
        {
            using var progress = EPlugin.Progress.Begin("Importing assets...");
            cancellation.Cancel();
            await Task.FromCanceled(cancellation.Token);
        }
        catch (OperationCanceledException) { }

        Assertions.AssertBool(_launched[1].Disposed).IsTrue();
        Assertions.AssertThat(_launched[1].Of(_launched[1].Users[0])).ContainsExactly("TEXT Importing assets...", "END");
        Assertions.AssertBool(_timer!.Disposed).IsTrue();
    }

    [TestCase]
    public void NestedScopesAreOneUserThatEndsWithTheOutermostScope()
    {
        var outer = ActivationProgress.Begin("Activating A...");
        using (ActivationProgress.Begin("Activating dependency..."))
        {
            ActivationProgress.SetText("Refreshing...");
        }

        Assertions.AssertBool(_launched[0].Disposed).IsFalse();
        outer.Dispose();
        outer.Dispose();
        var helper = _launched.Single();
        Assertions.AssertThat(helper.Users).HasSize(1);
        Assertions.AssertThat(helper.Of(helper.Users[0]))
            .ContainsExactly("TEXT Activating A...", "TEXT Activating dependency...", "TEXT Refreshing...", "END");
        // The connection is released, the helper keeps running for other editors.
        Assertions.AssertBool(helper.Disposed).IsTrue();
        Assertions.AssertBool(_timer!.Disposed).IsTrue();
    }

    [TestCase]
    public async Task ConcurrentOperationsAreSeparateUsersOfOneHelper()
    {
        var check = new TaskCompletionSource();
        var download = new TaskCompletionSource();
        var checking = Operation("Checking for updates...", check.Task, "Checked.");
        var downloading = Operation("Downloading...", download.Task, "Downloaded.");
        // A thread started outside any scope is a user of its own.
        await Task.Run(() =>
        {
            using var progress = ActivationProgress.Begin("Indexing...");
        });

        check.SetResult();
        await checking;
        Assertions.AssertBool(_launched[0].Disposed).IsFalse();
        download.SetResult();
        await downloading;

        var helper = _launched.Single();
        Assertions.AssertThat(helper.Users).HasSize(3);
        Assertions.AssertThat(helper.Of(helper.Users[0])).ContainsExactly("TEXT Checking for updates...", "TEXT Checked.", "END");
        Assertions.AssertThat(helper.Of(helper.Users[1])).ContainsExactly("TEXT Downloading...", "TEXT Downloaded.", "END");
        Assertions.AssertThat(helper.Of(helper.Users[2])).ContainsExactly("TEXT Indexing...", "END");
        Assertions.AssertBool(helper.Disposed).IsTrue();
    }

    private static async Task Operation(string text, Task work, string done)
    {
        using var progress = ActivationProgress.Begin(text);
        await work;
        // The async flow keeps its user, also when its continuation runs on another thread.
        await Task.Run(() => ActivationProgress.SetText(done));
    }

    [TestCase]
    public async Task ScopeReportsFromAnyThreadUntilItEnds()
    {
        var progress = ActivationProgress.Begin("Downloading...");
        await Task.Run(() => progress.SetText("Downloading... 50%"));
        progress.Dispose();
        await Task.Run(() => progress.SetText("Downloading... 100%"));
        var helper = _launched.Single();
        Assertions.AssertThat(helper.Of(helper.Users[0])).ContainsExactly("TEXT Downloading...", "TEXT Downloading... 50%", "END");
    }

    [TestCase]
    public async Task HeartbeatsAreSentForEveryActiveUser()
    {
        // Opened on other threads, so neither scope encloses the other.
        var first = await Task.Run(() => ActivationProgress.Begin("Activating A..."));
        var second = await Task.Run(() => ActivationProgress.Begin("Checking for updates..."));
        _tick!();
        first.Dispose();
        _tick();
        Assertions.AssertBool(_timer!.Disposed).IsFalse();
        second.Dispose();
        Assertions.AssertBool(_timer.Disposed).IsTrue();
        var helper = _launched.Single();
        Assertions.AssertThat(helper.Of(helper.Users[0])).ContainsExactly("TEXT Activating A...", "HEARTBEAT", "END");
        Assertions.AssertThat(helper.Of(helper.Users[1]))
            .ContainsExactly("TEXT Checking for updates...", "HEARTBEAT", "HEARTBEAT", "END");
    }

    [TestCase]
    public void HelperThatFailedAtStartupIsStartedAgainOnlyByTheNextOperation()
    {
        using (ActivationProgress.Begin("Activating A..."))
        {
            _launched[0].Dead = true;
            ActivationProgress.SetText("Activating B...");
            Assertions.AssertBool(_launched[0].Disposed).IsTrue();
            using (ActivationProgress.Begin("Activating C..."))
            {
                _tick!();
            }

            Assertions.AssertInt(_launched.Count).IsEqual(1);
        }

        using (ActivationProgress.Begin("Deactivating A..."))
        {
        }

        Assertions.AssertInt(_launched.Count).IsEqual(2);
        Assertions.AssertThat(_launched[1].Of(_launched[1].Users[0])).ContainsExactly("TEXT Deactivating A...", "END");
    }

    [TestCase]
    public void HelperThatExitedAfterRunningAWhileIsReplacedWithTheCurrentText()
    {
        using (ActivationProgress.Begin("Activating A..."))
        {
            _launched[0].Age = TimeSpan.FromMinutes(20);
            _launched[0].Dead = true;
            _tick!();
            Assertions.AssertInt(_launched.Count).IsEqual(2);
        }

        var user = _launched[0].Users[0];
        Assertions.AssertThat(_launched[1].Of(user)).ContainsExactly("TEXT Activating A...", "HEARTBEAT", "END");
    }

    [TestCase]
    public void RepeatedReplacementFailuresAreBoundedUntilTheNextOperation()
    {
        using (ActivationProgress.Begin("Activating A..."))
        {
            _launched[0].Age = TimeSpan.FromMinutes(20);
            _launched[0].Dead = true;
            ActivationProgress.Launch = () =>
            {
                // Stop the old recursive recovery path before it overflows the test process's stack.
                if (_launched.Count >= 5) return null;
                var helper = new FakeHelper { Age = TimeSpan.FromMinutes(20), Dead = true };
                _launched.Add(helper);
                return helper;
            };
            _tick!();
            _tick();
            ActivationProgress.SetText("Building...");
            Assertions.AssertInt(_launched.Count).IsEqual(2);
            Assertions.AssertBool(_launched.All(h => h.Disposed)).IsTrue();
        }

        ActivationProgress.Launch = () =>
        {
            var helper = new FakeHelper();
            _launched.Add(helper);
            return helper;
        };
        using (ActivationProgress.Begin("Activating B...")) { }
        Assertions.AssertInt(_launched.Count).IsEqual(3);
        Assertions.AssertThat(_launched[2].Of(_launched[2].Users[0])).ContainsExactly("TEXT Activating B...", "END");
    }

    [TestCase]
    public void ABusyCommandFileDropsOnlyThatCommandAndKeepsTheHelper()
    {
        using (ActivationProgress.Begin("Activating A..."))
        {
            // another editor held the command file longer than the wait limit
            _launched[0].Age = TimeSpan.FromMinutes(20);
            _launched[0].Busy = 1;
            _tick!();
            ActivationProgress.SetText("Building...");
        }

        // an old helper that failed would have been replaced at once
        Assertions.AssertInt(_launched.Count).IsEqual(1);
        Assertions.AssertThat(_launched[0].Of(_launched[0].Users[0])).ContainsExactly("TEXT Activating A...", "TEXT Building...", "END");
    }

    [TestCase]
    public void FailedAgeCheckStillDisposesTheHelper()
    {
        using (ActivationProgress.Begin("Activating A..."))
        {
            _launched[0].Dead = true;
            _launched[0].CannotCheckAge = true;
            _tick!();
            Assertions.AssertBool(_launched[0].Disposed).IsTrue();
            Assertions.AssertInt(_launched.Count).IsEqual(1);
        }
    }

    [TestCase]
    public async Task RestartHoldsTheWindowAndTheNextEditorContinuesTheUser()
    {
        var window = new FakeHelper();
        ActivationProgress.Launch = () =>
        {
            _launched.Add(window);
            return window;
        };
        using (ActivationProgress.Begin("Updating plugins..."))
        {
            ActivationProgress.PrepareForRestart();
            // The held user sends nothing more, so the helper keeps the hold; new operations show nothing.
            using (ActivationProgress.Begin("Shutdown callback...")) { }
            _tick!();
            await Task.Run(() =>
            {
                using var other = ActivationProgress.Begin("Checking for updates...");
            });
        }

        var user = window.Users.Single();
        Assertions.AssertThat(window.Of(user))
            .ContainsExactly("TEXT Updating plugins...", "TEXT Restarting the editor...", "HOLD 300");
        Assertions.AssertThat(window.HandedOver).ContainsExactly(user);

        ActivationProgress.Resume = () => (window, user);
        Assertions.AssertBool(ActivationProgress.ResumeAfterRestart()).IsTrue();
        using (ActivationProgress.Begin("Reconciling updated recipes..."))
        {
            using (ActivationProgress.Begin("Building...")) { }
            ActivationProgress.PrepareForRestart();
        }

        Assertions.AssertBool(ActivationProgress.ResumeAfterRestart()).IsTrue();
        using (ActivationProgress.Begin("Verifying plugins...")) { }
        Assertions.AssertInt(_launched.Count).IsEqual(1);
        Assertions.AssertThat(window.Users).ContainsExactly(user);
        Assertions.AssertThat(window.Of(user)).ContainsExactly("TEXT Updating plugins...", "TEXT Restarting the editor...",
            "HOLD 300", "HEARTBEAT", "TEXT Reconciling updated recipes...", "TEXT Building...", "TEXT Restarting the editor...",
            "HOLD 300", "HEARTBEAT", "TEXT Verifying plugins...", "END");
        Assertions.AssertThat(window.HandedOver).ContainsExactly(user, user);
    }

    [TestCase]
    public void UnavailableRestartSessionFallsBackToANewHelper()
    {
        ActivationProgress.Resume = () => throw new IOException("Session unavailable.");
        Assertions.AssertBool(ActivationProgress.ResumeAfterRestart()).IsFalse();
        using (ActivationProgress.Begin("Finishing update...")) { }
        Assertions.AssertInt(_launched.Count).IsEqual(1);
        Assertions.AssertBool(_launched[0].Disposed).IsTrue();
    }

    [TestCase]
    public void FailedRestartHandoffDoesNotAffectOperation()
    {
        using (ActivationProgress.Begin("Updating plugins..."))
        {
            _launched[0].CannotHandOver = true;
            ActivationProgress.PrepareForRestart();
        }

        Assertions.AssertBool(_launched[0].Disposed).IsTrue();
        Assertions.AssertBool(ActivationProgress.ResumeAfterRestart()).IsFalse();
    }

    [TestCase]
    public void MissingOrFailingHelperDoesNotAffectOperation()
    {
        ActivationProgress.Launch = () => null;
        using (ActivationProgress.Begin("Activating A..."))
        {
            ActivationProgress.SetText("Refreshing...");
            _tick!();
        }

        ActivationProgress.Launch = () => throw new IOException("dotnet not found");
        using (ActivationProgress.Begin("Activating A..."))
        {
            ActivationProgress.SetText("Refreshing...");
            _tick!();
            ActivationProgress.PrepareForRestart();
        }
    }

    [TestCase]
    public void SettingsDialogIsHiddenOnceAndRestoredAfterNestedOperation()
    {
        var dialog = new FakeDisposable();
        var hideCount = 0;
        ActivationProgress.HideSettingsDialog = () =>
        {
            hideCount++;
            return dialog;
        };

        using (ActivationProgress.Begin("Activating A..."))
        {
            using (ActivationProgress.Begin("Activating dependency..."))
            {
            }

            Assertions.AssertInt(hideCount).IsEqual(1);
            Assertions.AssertBool(dialog.Disposed).IsFalse();
            // A failed progress helper must not restore settings while installation is still running.
            _launched[0].Dead = true;
            ActivationProgress.SetText("Rebuilding...");
            Assertions.AssertBool(dialog.Disposed).IsFalse();
        }

        Assertions.AssertBool(dialog.Disposed).IsTrue();
    }

    [TestCase]
    public static void SettingsDialogIsRestoredWhenOperationThrows()
    {
        var dialog = new FakeDisposable();
        ActivationProgress.HideSettingsDialog = () => dialog;
        try
        {
            using var progress = ActivationProgress.Begin("Activating A...");
            throw new InvalidOperationException("Installation failed.");
        }
        catch (InvalidOperationException)
        {
        }

        Assertions.AssertBool(dialog.Disposed).IsTrue();
    }

    [TestCase]
    public static void SettingsDialogIsLeftAloneByOperationsOutsideTheEditorThread()
    {
        var hidden = false;
        ActivationProgress.HideSettingsDialog = () =>
        {
            hidden = true;
            return null;
        };
        ActivationProgress.IsEditorThread = () => false;
        using (ActivationProgress.Begin("Indexing..."))
        {
        }

        Assertions.AssertBool(hidden).IsFalse();
    }

    [TestCase]
    public static void SettingsDialogIsHiddenByAnEditorThreadOperationThatJoinsOneFromAnotherThread()
    {
        var hidden = 0;
        ActivationProgress.HideSettingsDialog = () =>
        {
            hidden++;
            return null;
        };
        ActivationProgress.IsEditorThread = () => false;
        using (ActivationProgress.Begin("Indexing..."))
        {
            ActivationProgress.IsEditorThread = () => true;
            using (ActivationProgress.Begin("Activating A..."))
            {
            }

            using (ActivationProgress.Begin("Activating B..."))
            {
            }
        }

        Assertions.AssertInt(hidden).IsEqual(1);
    }

    [TestCase]
    public void FailingSettingsDialogDoesNotAffectOperation()
    {
        ActivationProgress.HideSettingsDialog = () => throw new InvalidOperationException("Editor UI unavailable.");
        using (ActivationProgress.Begin("Activating A..."))
        {
        }

        Assertions.AssertBool(_launched[0].Disposed).IsTrue();
    }

    [TestCase]
    [RequireGodotRuntime]
    public static void ProcessHelperIsNotStartedWhenAssemblyIsMissing()
    {
        ActivationProgress.IsEditorThread = () => true;
        Assertions.AssertObject(ProcessProgressHelper.TryStart("res://addons/ePlugin/progress/Missing.dll")).IsNull();
    }

    private sealed class FakeDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeHelper : IProgressHelper
    {
        private readonly List<(string User, string Command)> _received = [];

        /// <summary>The users in the order of their first command.</summary>
        public List<string> Users => _received.Select(r => r.User).Distinct().ToList();

        public List<string> HandedOver { get; } = [];
        public bool Dead { get; set; }
        /// <summary>How many of the next commands find the command file busy.</summary>
        public int Busy { get; set; }
        public bool Disposed { get; private set; }
        public bool CannotHandOver { get; set; }
        public bool CannotCheckAge { get; set; }
        private TimeSpan _age = TimeSpan.FromSeconds(1);
        public TimeSpan Age
        {
            get => CannotCheckAge ? throw new IOException("Process unavailable.") : _age;
            set => _age = value;
        }

        /// <summary>A user's commands, with TEXT payloads decoded.</summary>
        public List<string> Of(string user)
        {
            lock (_received) return _received.Where(r => r.User == user).Select(r => r.Command).ToList();
        }

        public void Send(string user, string command)
        {
            if (Dead) throw new IOException("The helper exited.");
            if (Busy > 0) { Busy--; throw new ProgressHelperBusyException(); }
            if (command.StartsWith("TEXT ", StringComparison.Ordinal))
                command = "TEXT " + Encoding.UTF8.GetString(Convert.FromBase64String(command[5..]));
            lock (_received) _received.Add((user, command));
        }

        public void HandOver(string user)
        {
            if (CannotHandOver) throw new IOException("Cannot persist session.");
            HandedOver.Add(user);
        }

        public void Dispose() => Disposed = true;
    }
}
#endif
