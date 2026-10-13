#if TOOLS
namespace Enaweg.Plugin;

/// <summary>Creates progress scopes for editor work in ePlugin's shared native progress window.</summary>
public interface IEPluginProgress
{
    /// <summary>Begins an operation and reports its initial text.</summary>
    /// <param name="text">The current step to display.</param>
    /// <returns>A non-null scope to dispose when the operation finishes, fails, or is canceled.</returns>
    /// <remarks>
    /// Nested scopes share their enclosing operation's user; independent operations share the window as separate
    /// users. Begin on the editor thread to allow a helper to be started, then report from any thread. Feedback is
    /// best effort: a missing or failing helper does not fail the operation. This service leaves Project Settings
    /// visible and does not control cancellation or editor restarts.
    /// </remarks>
    IEPluginProgressScope Begin(string text);
}
#endif
