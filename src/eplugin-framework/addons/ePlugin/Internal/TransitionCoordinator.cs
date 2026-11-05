#if TOOLS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// What keeps a plugin operation (<see cref="EGlobal"/>'s transition) from finishing besides its participants: the
/// plugins whose activation or deactivation waits for other plugins, the holds that keep it open while several plugins
/// are toggled, and the watchdog that gives up on one that makes no progress. The transition's participants and its
/// recorded attempt stay in <see cref="EGlobal"/>, which decides what waits and when.
/// </summary>
internal sealed class TransitionCoordinator(TimeSpan stallTimeout, ulong stallFrames)
{
    private int _holds;

    /// <summary>
    /// The plugins whose activation waits: for a hard dependency to activate, whose activation resumes them, or for the
    /// framework to be initialized.
    /// </summary>
    public WaitingStack<PluginContext> Activations { get; } = new();

    /// <summary>The plugins whose deactivation waits for their activated dependants to be deactivated first.</summary>
    public WaitingStack<PluginContext> Deactivations { get; } = new();

    /// <summary>
    /// Plugins that enabled the framework because it was disabled: unlike the ones a start finds enabled, they still have
    /// to be installed once the framework is initialized.
    /// </summary>
    public HashSet<PluginContext> EnabledWithFramework { get; } = [];

    /// <summary>Gives up on a plugin operation that makes no progress, see <see cref="StallWatchdog"/>.</summary>
    public StallWatchdog Watchdog { get; } = new(stallTimeout, stallFrames);

    /// <summary>Whether a <see cref="Hold"/> keeps the operation open.</summary>
    public bool IsHeld => _holds > 0;

    /// <summary>Whether plugins wait for others to activate or deactivate.</summary>
    public bool HasWaiting => !Activations.IsEmpty || !Deactivations.IsEmpty;

    /// <summary>Whether the operation cannot finish yet: plugins wait, or it is held.</summary>
    public bool IsPending => HasWaiting || IsHeld;

    /// <summary>
    /// Keeps the operation open until the result is disposed, also after its last participant finished, so several
    /// plugins toggled one after another are built once. Holds nest.
    /// </summary>
    public IDisposable Hold()
    {
        _holds++;
        return new Release(this);
    }

    /// <summary>Stops every activation and deactivation from waiting, e.g. when the operation failed.</summary>
    public void ClearWaiting()
    {
        Activations.Clear();
        Deactivations.Clear();
    }

    /// <summary>The plugins whose activation waits for <paramref name="slug"/> as a hard dependency.</summary>
    public IEnumerable<PluginContext> WaitingFor(string slug) =>
        Activations.Where(c => c.Slug != slug && c.IsRecipeCreated &&
                               c.Builder.PluginRecipe.PluginDependencies.Any(d => d.Slug == slug));

    private sealed class Release(TransitionCoordinator coordinator) : IDisposable
    {
        private TransitionCoordinator? _coordinator = coordinator;

        public void Dispose()
        {
            if (_coordinator is null) return;
            _coordinator._holds--;
            _coordinator = null;
        }
    }
}

/// <summary>
/// A stack of items that wait, the last one pushed on top, which can also drop items from anywhere while keeping the
/// order of the others. Enumerates from the top, like <see cref="Stack{T}"/>.
/// </summary>
internal sealed class WaitingStack<T> : IEnumerable<T>
{
    private readonly Stack<T> _items = new();

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    public void Push(T item) => _items.Push(item);

    public T Pop() => _items.Pop();

    public bool Contains(T item) => _items.Contains(item);

    public void Clear() => _items.Clear();

    /// <summary>Takes every item off the stack; returns them from the top.</summary>
    public T[] TakeAll()
    {
        var items = _items.ToArray();
        _items.Clear();
        return items;
    }

    /// <summary>Drops the items that match <paramref name="drop"/>, every time they were pushed; the others keep their order.</summary>
    public void RemoveWhere(Func<T, bool> drop)
    {
        var kept = _items.Where(item => !drop(item)).Reverse().ToArray();
        _items.Clear();
        foreach (var item in kept) _items.Push(item);
    }

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Tells when an operation made no progress: its signature, which changes with any progress, stayed the same for both
/// a time and a number of frames. A frame that took long is work rather than a stall, and so are many quick frames.
/// </summary>
internal sealed class StallWatchdog(TimeSpan timeout, ulong frames)
{
    private string? _signature;
    private DateTime _since;
    private ulong _frame;

    /// <summary>
    /// Observes the operation at <paramref name="signature"/>; true once it has not changed for the timeout and the
    /// frames. A new signature starts counting anew.
    /// </summary>
    public bool IsStalled(string signature, DateTime now, ulong frame)
    {
        if (signature != _signature)
        {
            _signature = signature;
            _since = now;
            _frame = frame;
            return false;
        }

        return now - _since >= timeout && frame - _frame >= frames;
    }

    /// <summary>Forgets what was observed: the next observation counts the whole timeout again.</summary>
    public void Reset() => _signature = null;
}
#endif
