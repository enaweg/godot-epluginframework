using System.Collections.Generic;
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class PluginTogglesTests
{
    [TestCase]
    public void ReportsEnabledAndDisabledPluginsInOrder()
    {
        var toggles = new PluginToggles(["ePlugin", "kept", "removed_b", "removed_a"]);

        var (enabled, disabled) = toggles.Changes(Set("ePlugin", "kept", "new_b", "new_a"));

        Assertions.AssertThat(enabled).ContainsExactly("new_a", "new_b");
        Assertions.AssertThat(disabled).ContainsExactly("removed_a", "removed_b");
    }

    [TestCase]
    public void RebaseTakesTheCurrentPluginsAsHandled()
    {
        var toggles = new PluginToggles(["a"]);
        toggles.Rebase(Set("b"));

        var (enabled, disabled) = toggles.Changes(Set("b"));

        Assertions.AssertThat(enabled).IsEmpty();
        Assertions.AssertThat(disabled).IsEmpty();
        Assertions.AssertThat(toggles.Known).ContainsExactly("b");
    }

    [TestCase]
    public void NotedTogglesAreNotReportedAgain()
    {
        // e.g. a plain dependency the framework enabled itself, and a plugin it disabled for its license
        var toggles = new PluginToggles(["licensed"]);
        toggles.Note("dependency", true);
        toggles.Note("licensed", false);

        var (enabled, disabled) = toggles.Changes(Set("dependency"));

        Assertions.AssertThat(enabled).IsEmpty();
        Assertions.AssertThat(disabled).IsEmpty();
    }

    [TestCase]
    public void PluginDisabledAndEnabledAgainBetweenTwoLooksIsNoChange()
    {
        var toggles = new PluginToggles(["plain"]);

        var (enabled, disabled) = toggles.Changes(Set("plain"));

        Assertions.AssertThat(enabled).IsEmpty();
        Assertions.AssertThat(disabled).IsEmpty();
    }

    private static HashSet<string> Set(params string[] slugs) => [.. slugs];
}
