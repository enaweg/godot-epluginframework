#if TOOLS
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class EditorThreadQueueTests
{
    [BeforeTest]
    public void Setup() => EGlobal.RunPosted();

    [TestCase]
    public void PostedWorkRunsInOrderAndWorkPostedWhileRunningWaitsForTheNextFrame()
    {
        var ran = new List<string>();
        EGlobal.Post(() => { ran.Add("first"); EGlobal.Post(() => ran.Add("posted while running")); });
        EGlobal.Post(() => ran.Add("second"));
        EGlobal.RunPosted();
        Assertions.AssertThat(ran).ContainsExactly("first", "second");
        EGlobal.RunPosted();
        Assertions.AssertThat(ran).ContainsExactly("first", "second", "posted while running");
    }

    [TestCase]
    public void WorkPostedFromAnotherThreadCompletesItsTaskOnTheNextRun()
    {
        var task = Task.Factory.StartNew(() => EGlobal.OnEditorThread(() => { }, CancellationToken.None)).Result;
        Assertions.AssertBool(task.IsCompleted).IsFalse();
        EGlobal.RunPosted();
        Assertions.AssertBool(task.Wait(1000)).IsTrue();
    }

    [TestCase]
    public void CanceledWorkIsSkipped()
    {
        using var cancel = new CancellationTokenSource();
        var ran = false;
        var task = EGlobal.OnEditorThread(() => ran = true, cancel.Token);
        cancel.Cancel();
        EGlobal.RunPosted();
        Assertions.AssertBool(ran).IsFalse();
        Assertions.AssertBool(task.IsCanceled).IsTrue();
    }
}
#endif
