using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class ActivationProgressTests
{
    private readonly List<FakeHelper> _launched = [];

    [BeforeTest]
    public void Setup()
    {
        _launched.Clear();
        ActivationProgress.Launch = () =>
        {
            var helper = new FakeHelper();
            _launched.Add(helper);
            return helper;
        };
    }

    [AfterTest]
    public void Cleanup()
    {
        ActivationProgress.Launch = ActivationProgress.LaunchDefault;
    }

    [TestCase]
    public void BeginLaunchesHelperAndSendsText()
    {
        using (ActivationProgress.Begin("Activating A..."))
        {
            ActivationProgress.SetText("Refreshing...");
        }

        Assertions.AssertInt(_launched.Count).IsEqual(1);
        Assertions.AssertThat(_launched[0].Received).ContainsExactly("Activating A...", "Refreshing...");
        Assertions.AssertBool(_launched[0].Disposed).IsTrue();
    }

    [TestCase]
    public void HeartbeatIsSentToRunningHelper()
    {
        using (ActivationProgress.Begin("Activating A..."))
        {
            ActivationProgress.Heartbeat();
            ActivationProgress.SetText("Activating B...");
            ActivationProgress.Heartbeat();
        }

        Assertions.AssertThat(_launched[0].Received)
            .ContainsExactly("Activating A...", "HEARTBEAT", "Activating B...", "HEARTBEAT");
    }

    [TestCase]
    public void NestedScopesShareOneHelperUntilOutermostDisposed()
    {
        var outer = ActivationProgress.Begin("Activating A...");
        using (ActivationProgress.Begin("Activating B..."))
        {
        }

        Assertions.AssertInt(_launched.Count).IsEqual(1);
        Assertions.AssertBool(_launched[0].Disposed).IsFalse();

        outer.Dispose();

        Assertions.AssertBool(_launched[0].Disposed).IsTrue();
        Assertions.AssertThat(_launched[0].Received).ContainsExactly("Activating A...", "Activating B...");
    }

    [TestCase]
    public void DisposingScopeTwiceClosesOnlyOnce()
    {
        var outer = ActivationProgress.Begin("Activating A...");
        var inner = ActivationProgress.Begin("Activating B...");
        inner.Dispose();
        inner.Dispose();

        Assertions.AssertBool(_launched[0].Disposed).IsFalse();

        outer.Dispose();

        Assertions.AssertBool(_launched[0].Disposed).IsTrue();
    }

    [TestCase]
    public void DeadHelperIsNotRestartedWithinOperationButIsForTheNextOne()
    {
        using (ActivationProgress.Begin("Activating A..."))
        {
            _launched[0].Dead = true;
            ActivationProgress.SetText("Activating B...");

            Assertions.AssertBool(_launched[0].Disposed).IsTrue();

            using (ActivationProgress.Begin("Activating C..."))
            {
            }

            Assertions.AssertInt(_launched.Count).IsEqual(1);
        }

        using (ActivationProgress.Begin("Deactivating A..."))
        {
        }

        Assertions.AssertInt(_launched.Count).IsEqual(2);
        Assertions.AssertThat(_launched[1].Received).ContainsExactly("Deactivating A...");
    }

    [TestCase]
    public void MissingOrFailingHelperDoesNotAffectOperation()
    {
        ActivationProgress.Launch = () => null;
        using (ActivationProgress.Begin("Activating A..."))
        {
            ActivationProgress.SetText("Refreshing...");
            ActivationProgress.Heartbeat();
        }

        ActivationProgress.Launch = () => throw new IOException("dotnet not found");
        using (ActivationProgress.Begin("Activating A..."))
        {
            ActivationProgress.SetText("Refreshing...");
            ActivationProgress.Heartbeat();
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ProcessHelperIsNotStartedWhenAssemblyIsMissing()
    {
        Assertions.AssertObject(ProcessProgressHelper.TryStart("res://addons/ePlugin/progress/Missing.dll")).IsNull();
    }

    private sealed class FakeHelper : IProgressHelper
    {
        /// <summary>Decoded TEXT payloads; any other command verbatim.</summary>
        public List<string> Received { get; } = [];

        public bool Dead { get; set; }

        public bool Disposed { get; private set; }

        public void Send(string command)
        {
            if (Dead)
            {
                throw new IOException("The helper exited.");
            }

            Received.Add(command.StartsWith("TEXT ", StringComparison.Ordinal)
                ? Encoding.UTF8.GetString(Convert.FromBase64String(command[5..]))
                : command);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
