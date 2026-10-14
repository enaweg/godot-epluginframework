#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Enaweg.Plugin.Internal.Update;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class UpdateGroupsTests
{
    [TestCase]
    public static void IndependentPluginsAreGroupedAloneInBatchOrder()
    {
        var groups = Plan(["b", "a", "c"], new() { ["a"] = ["ePlugin"], ["b"] = ["ePlugin"] });
        Assertions.AssertThat(groups).ContainsExactly("b", "a", "c");
    }

    [TestCase]
    public static void DependantJoinsItsDependencyAlsoThroughAPluginThatIsNotUpdated()
    {
        // app -> lib directly, tool -> bridge -> lib through a plugin outside the batch
        var groups = Plan(["app", "solo", "tool", "lib"], new()
        {
            ["app"] = ["lib"], ["tool"] = ["bridge"], ["bridge"] = ["lib"], ["solo"] = ["ePlugin"]
        });
        Assertions.AssertThat(groups).ContainsExactly("app,tool,lib", "solo");
    }

    [TestCase]
    public static void SharedDependencyOutsideTheBatchDoesNotJoinGroups()
    {
        var groups = Plan(["a", "b"], new() { ["a"] = ["logging"], ["b"] = ["logging"] });
        Assertions.AssertThat(groups).ContainsExactly("a", "b");
    }

    [TestCase]
    public static void FrameworkIsGroupedAloneAndFirst()
    {
        var groups = Plan(["a", "ePlugin", "b"], new() { ["a"] = ["ePlugin"], ["b"] = ["ePlugin", "a"] });
        Assertions.AssertThat(groups).ContainsExactly("ePlugin", "a,b");
    }

    private static string[] Plan(string[] batch, Dictionary<string, string[]> dependencies) =>
        UpdateGroups.Plan(batch, dependencies.ToDictionary(p => p.Key, p => (IReadOnlyCollection<string>)p.Value), "ePlugin")
            .Select(g => string.Join(",", g)).ToArray();
}
#endif
