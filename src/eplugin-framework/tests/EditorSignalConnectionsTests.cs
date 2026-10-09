using Enaweg.Plugin.Internal.Manager;
using GdUnit4;
using Godot;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class EditorSignalConnectionsTests
{
    [TestCase]
    public void CleanupDisconnectsOwnedCallbacksAndPreservesOtherSubscribers()
    {
        var first = new Button();
        var second = new Button();
        using var signals = new EditorSignalConnections();
        try
        {
            var ownedCalls = 0;
            var otherCalls = 0;
            signals.Connect(first, BaseButton.SignalName.Pressed, Callable.From(() => ownedCalls++));
            signals.Connect(second, BaseButton.SignalName.Pressed, Callable.From(() => ownedCalls++));
            second.Pressed += () => otherCalls++;
            first.EmitSignal(BaseButton.SignalName.Pressed);
            second.EmitSignal(BaseButton.SignalName.Pressed);
            Assertions.AssertInt(ownedCalls).IsEqual(2);

            signals.Dispose();
            signals.Dispose();
            first.EmitSignal(BaseButton.SignalName.Pressed);
            second.EmitSignal(BaseButton.SignalName.Pressed);
            Assertions.AssertInt(ownedCalls).IsEqual(2);
            Assertions.AssertInt(otherCalls).IsEqual(2);
            Assertions.AssertInt(first.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count).IsEqual(0);
        }
        finally { first.Free(); second.Free(); }
    }

    [TestCase]
    public void CleanupBeforeTargetDisposalRemovesCrossNodeCallbacks()
    {
        var source = new Button();
        var target = new Window();
        var targetId = target.GetInstanceId();
        using var signals = new EditorSignalConnections();
        try
        {
            signals.Connect(source, BaseButton.SignalName.Pressed, Callable.From(target.Hide));
            // Godot serializes all scripts before disposing any: cleanup must run while the target is valid.
            signals.Dispose();
            target.Dispose();
            signals.Dispose();
            source.EmitSignal(BaseButton.SignalName.Pressed);
            Assertions.AssertInt(source.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count).IsEqual(0);
        }
        finally
        {
            // Disposing a Node wrapper leaves its native object alive, just as it does during editor reloads.
            source.Free();
            if (GodotObject.InstanceFromId(targetId) is Node nativeTarget) nativeTarget.Free();
        }
    }

    [TestCase]
    public void CleanupToleratesFreedSourcesAndAlreadyDisconnectedCallbacks()
    {
        var source = new Button();
        var freed = new Button();
        using var signals = new EditorSignalConnections();
        try
        {
            var callback = Callable.From(() => { });
            signals.Connect(source, BaseButton.SignalName.Pressed, callback);
            signals.Connect(freed, BaseButton.SignalName.Pressed, callback);
            source.Disconnect(BaseButton.SignalName.Pressed, callback);
            freed.Free();
            signals.Dispose();
            Assertions.AssertInt(source.GetSignalConnectionList(BaseButton.SignalName.Pressed).Count).IsEqual(0);
        }
        finally { source.Free(); }
    }
}
