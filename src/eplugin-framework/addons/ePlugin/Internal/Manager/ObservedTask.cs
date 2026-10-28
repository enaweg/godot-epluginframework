#if TOOLS
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Starts asynchronous work from a Godot signal, which cannot await it. An <c>async void</c> handler would rethrow an
/// unexpected exception on the editor's synchronization context and take the editor down; this reports it instead.
/// </summary>
internal static class ObservedTask
{
    /// <param name="report">
    /// Receives an unexpected exception, on whichever thread the work ended (it may be a thread-pool thread); hand anything
    /// that touches Godot to the editor thread, e.g. with <c>CallDeferred(MethodName.X, ...)</c>.
    /// </param>
    public static void Run(Task task, Action<Exception> report)
    {
        task.ContinueWith(t => report(t.Exception!.GetBaseException()), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
#endif
