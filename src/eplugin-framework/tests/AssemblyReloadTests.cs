using System;
using Enaweg.Plugin.Internal;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class AssemblyReloadTests
{
    private static readonly DateTime Loaded = new(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc);

    [TestCase]
    public void TheLoadedBuildOrAnOlderOneIsNoReload()
    {
        DateTime? loaded = Loaded, since = null;
        Assertions.AssertObject(AssemblyReload.Decide(ref loaded, Loaded, Loaded.AddMinutes(5), ref since)).IsEqual(AssemblyReload.State.Current);
        Assertions.AssertObject(AssemblyReload.Decide(ref loaded, Loaded.AddMinutes(-1), Loaded.AddMinutes(5), ref since)).IsEqual(AssemblyReload.State.Current);
        // Nothing captured or no build on disk: never block.
        DateTime? unknown = null;
        Assertions.AssertObject(AssemblyReload.Decide(ref unknown, Loaded, Loaded, ref since)).IsEqual(AssemblyReload.State.Current);
        Assertions.AssertObject(AssemblyReload.Decide(ref loaded, null, Loaded, ref since)).IsEqual(AssemblyReload.State.Current);
    }

    [TestCase]
    public void ANewerBuildIsPendingUntilItIsLoadedOrWaitedForTooLong()
    {
        DateTime? loaded = Loaded, since = null;
        var built = Loaded.AddSeconds(30);
        var now = built.AddSeconds(1);
        Assertions.AssertObject(AssemblyReload.Decide(ref loaded, built, now, ref since)).IsEqual(AssemblyReload.State.Pending);
        Assertions.AssertObject(AssemblyReload.Decide(ref loaded, built, now + AssemblyReload.GiveUpAfter - TimeSpan.FromSeconds(1), ref since))
            .IsEqual(AssemblyReload.State.Pending);
        Assertions.AssertObject(AssemblyReload.Decide(ref loaded, built, now + AssemblyReload.GiveUpAfter, ref since)).IsEqual(AssemblyReload.State.GaveUp);
        // Giving up accepts that build, so it no longer blocks, but a later build is waited for again.
        Assertions.AssertObject(AssemblyReload.Decide(ref loaded, built, now + AssemblyReload.GiveUpAfter * 2, ref since)).IsEqual(AssemblyReload.State.Current);
        Assertions.AssertObject(AssemblyReload.Decide(ref loaded, built.AddMinutes(1), now.AddMinutes(2), ref since)).IsEqual(AssemblyReload.State.Pending);
    }
}
