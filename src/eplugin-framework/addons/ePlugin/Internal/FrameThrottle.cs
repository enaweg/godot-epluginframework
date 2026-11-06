#if TOOLS
namespace Enaweg.Plugin.Internal;

/// <summary>
/// Lets work that is polled every frame run only every <see cref="Interval"/> frames. A mutable struct: keep it in a
/// field that is not readonly, or each call changes a copy.
/// </summary>
internal struct FrameThrottle(ulong interval)
{
    private ulong _next;

    /// <summary>The frames from one run of the work to the next.</summary>
    public ulong Interval { get; } = interval;

    /// <summary>
    /// Whether the work is due in <paramref name="frame"/>; when it is, it is due next <see cref="Interval"/> frames later.
    /// </summary>
    public bool Due(ulong frame)
    {
        if (frame < _next)
        {
            return false;
        }

        _next = frame + Interval;
        return true;
    }

    /// <summary>Makes the work due at the next call, whatever its frame.</summary>
    public void Reset() => _next = 0;
}
#endif
