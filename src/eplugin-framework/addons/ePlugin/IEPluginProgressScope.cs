#if TOOLS
using System;

namespace Enaweg.Plugin;

/// <summary>A disposable operation that reports step text through <see cref="IProgress{T}"/>.</summary>
/// <remarks>
/// Reports are synchronous and thread-safe; they do not dispatch through the editor's synchronization context.
/// Disposal is idempotent and subsequent reports are ignored. Dispose every scope when its work ends, including
/// on failure or cancellation. A nested scope ending leaves its enclosing operation active and keeps the latest text.
/// The scope reports feedback only; the operation owns its cancellation token and result.
/// </remarks>
public interface IEPluginProgressScope : IProgress<string>, IDisposable
{
}
#endif
