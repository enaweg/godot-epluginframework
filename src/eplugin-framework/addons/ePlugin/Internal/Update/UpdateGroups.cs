#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>
/// Splits an update batch into groups that are installed, built and rolled back on their own, so one failing plugin no
/// longer reverts the whole batch.
/// </summary>
internal static class UpdateGroups
{
    /// <summary>
    /// Plugins of the batch end up in one group when one depends on the other, also through plugins that are not
    /// updated, because a dependant may need its dependency's new version to compile. Every ePlugin depends on the
    /// framework, so that dependency does not join groups; the framework's own group comes first, so later groups build
    /// against the new framework. Otherwise groups keep the order of <paramref name="batch"/>.
    /// </summary>
    /// <param name="dependencies">The hard plugin dependencies of every enabled plugin, by slug.</param>
    public static IReadOnlyList<IReadOnlyList<string>> Plan(IReadOnlyList<string> batch,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependencies, string framework)
    {
        var parent = batch.ToDictionary(slug => slug, slug => slug, StringComparer.Ordinal);
        string Root(string slug)
        {
            while (parent[slug] != slug) slug = parent[slug] = parent[parent[slug]];
            return slug;
        }
        foreach (var slug in batch.Where(s => s != framework))
            foreach (var reached in Reachable(slug, dependencies, framework).Where(parent.ContainsKey))
                parent[Root(reached)] = Root(slug);
        return batch.GroupBy(Root, StringComparer.Ordinal).Select(g => (IReadOnlyList<string>)g.ToArray())
            .OrderBy(g => g.Contains(framework) ? 0 : 1).ToArray();
    }

    private static HashSet<string> Reachable(string slug, IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependencies, string framework)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([slug]);
        while (pending.TryPop(out var current))
            foreach (var dependency in dependencies.GetValueOrDefault(current) ?? [])
                if (dependency != framework && reached.Add(dependency)) pending.Push(dependency);
        return reached;
    }
}
#endif
