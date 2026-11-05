#if TOOLS
using Enaweg.Plugin.Internal.Manager;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

/// <summary>The ePlugin Manager's busy state, without the editor.</summary>
[TestSuite]
public class ManagerSessionTests
{
    [TestCase]
    public static void AnIdleManagerIsBusyOnlyWhilePluginsChangeOrAReloadIsDue()
    {
        var session = new ManagerSession();
        Assertions.AssertBool(session.IsBusy(changingPlugins: false, reloadPending: false)).IsFalse();
        Assertions.AssertBool(session.IsBusy(changingPlugins: true, reloadPending: false)).IsTrue();
        Assertions.AssertBool(session.IsBusy(changingPlugins: false, reloadPending: true)).IsTrue();
        Assertions.AssertBool(session.LocksInput(false, false)).IsFalse();
        Assertions.AssertBool(session.CanClose(false, false)).IsTrue();
        Assertions.AssertBool(session.CanClose(false, reloadPending: true)).IsFalse();
        Assertions.AssertBool(session.RefreshOnCompletion).IsTrue();
    }

    [TestCase]
    public static void CheckingOrStagingIsBusyButLeavesTheInputOpenToCancel()
    {
        var session = new ManagerSession { Working = true, Installing = true };

        Assertions.AssertBool(session.IsBusy(false, false)).IsTrue();
        Assertions.AssertBool(session.LocksInput(false, false)).IsFalse();
        Assertions.AssertBool(session.ShowsCancel).IsTrue();
        Assertions.AssertBool(session.CanClose(false, false)).IsTrue();

        // replacing addon files can no longer be canceled
        session.Swapping = true;
        Assertions.AssertBool(session.ShowsCancel).IsFalse();
        Assertions.AssertBool(session.CanClose(false, false)).IsFalse();

        session.EndWork();
        Assertions.AssertBool(session.Working || session.Installing || session.Swapping).IsFalse();
        Assertions.AssertBool(session.IsBusy(false, false)).IsFalse();
    }

    [TestCase]
    public static void APluginOperationLocksTheInputUntilItCompletesAndItsReloadIsDone()
    {
        var session = new ManagerSession();
        Assertions.AssertBool(session.Operation.TryBegin()).IsTrue();
        session.Working = true;
        Assertions.AssertBool(session.LocksInput(false, false)).IsTrue();
        Assertions.AssertBool(session.CanClose(false, false)).IsFalse();
        // its work runs, or dependencies still change
        Assertions.AssertBool(session.CanCompleteOperation(changingPlugins: false)).IsFalse();
        session.Working = false;
        Assertions.AssertBool(session.CanCompleteOperation(changingPlugins: true)).IsFalse();
        Assertions.AssertBool(session.CanCompleteOperation(changingPlugins: false)).IsTrue();

        session.Operation.Complete(reloadPending: true);
        Assertions.AssertBool(session.CanCompleteOperation(false)).IsFalse();
        // the reload that replaces the manager unlocks it, never the old one
        Assertions.AssertBool(session.IsBusy(false, false)).IsTrue();
        Assertions.AssertBool(session.LocksInput(false, false)).IsTrue();
    }

    [TestCase]
    public static void TheListIsReadAgainOnceNothingLocksIt()
    {
        var session = new ManagerSession { RefreshWhenIdle = true, Working = true };
        Assertions.AssertBool(session.ShouldRefresh(locked: false)).IsFalse();
        session.Working = false;
        Assertions.AssertBool(session.ShouldRefresh(locked: true)).IsFalse();
        Assertions.AssertBool(session.ShouldRefresh(locked: false)).IsTrue();
        session.RefreshWhenIdle = false;
        Assertions.AssertBool(session.ShouldRefresh(locked: false)).IsFalse();
    }
}
#endif
