#if TOOLS
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// Work for the editor thread: queued from any thread and run by <c>EPluginPlugin._Process</c>, never handed to Godot
/// as a delegate, see "Bootstrap and lifecycle" in CLAUDE.md.
/// </summary>
internal sealed partial class EGlobal
{
    internal static Task OnEditorThread(Action action, CancellationToken ct)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => completion.TrySetCanceled(ct));
        Post(() =>
        {
            try { if (!ct.IsCancellationRequested) { action(); completion.TrySetResult(); } }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { registration.Dispose(); }
        });
        return completion.Task;
    }

    // Work for the editor thread, run by EPluginPlugin._Process. Unlike Callable.CallDeferred it hands Godot no delegate:
    // a C# assembly reload discards this queue with the rest of the old assembly's static state, so nothing queued by
    // old code runs after the reload, and background threads never call into Godot.
    private static readonly ConcurrentQueue<Action> Posted = new();

    /// <summary>Runs <paramref name="action"/> on the editor thread in a later frame. Callable from any thread.</summary>
    internal static void Post(Action action) => Posted.Enqueue(action);

    // Set by Detach: the framework was disabled and Godot freed its plugin.
    private bool _detached;

    /// <summary>
    /// Like <see cref="Post"/>, for work on this instance's plugins: skipped when the framework was disabled before it
    /// ran. The queue outlives the instance, and an instance created when the framework is enabled again picks up the
    /// state itself while it initializes.
    /// </summary>
    internal void PostForInstance(Action action) => Post(() => { if (!_detached) action(); });

    /// <summary>Drops the work posted so far, as an assembly reload does.</summary>
    internal static void DiscardPosted() => Posted.Clear();

    /// <summary>Runs the work posted so far; called once per frame on the editor thread.</summary>
    internal static void RunPosted()
    {
        // Work posted while running waits for the next frame, so a chain of posts cannot keep the frame from ending.
        for (var count = Posted.Count; count > 0 && Posted.TryDequeue(out var action); count--)
        {
            try { action(); }
            catch (Exception ex) { GD.PushError($"ePlugin editor-thread work failed: {ex}"); }
        }
    }
}
#endif
