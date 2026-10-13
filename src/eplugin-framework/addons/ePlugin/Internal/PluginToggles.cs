#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// The plugins that were enabled when the framework last looked, to tell which ones were enabled or disabled since.
/// Plain plugins run no framework code when they are toggled, so this is how the framework notices them.
/// </summary>
internal sealed class PluginToggles(IEnumerable<string> known)
{
    private readonly HashSet<string> _known = new(known, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Known => _known;

    /// <summary>The plugins enabled and disabled since the last <see cref="Rebase"/> or <see cref="Note"/>.</summary>
    public (string[] Enabled, string[] Disabled) Changes(IReadOnlySet<string> current) =>
        (current.Where(slug => !_known.Contains(slug)).Order(StringComparer.Ordinal).ToArray(),
            _known.Where(slug => !current.Contains(slug)).Order(StringComparer.Ordinal).ToArray());

    /// <summary>Takes the current plugins as handled.</summary>
    public void Rebase(IEnumerable<string> current)
    {
        _known.Clear();
        _known.UnionWith(current);
    }

    /// <summary>Records a toggle the framework handled itself, so it is not reported as a change.</summary>
    public void Note(string slug, bool enabled)
    {
        if (enabled) _known.Add(slug);
        else _known.Remove(slug);
    }
}
#endif
