using System;
using System.IO;
using Enaweg.Plugin.Internal.Manager;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
public class ManagerReopenMarkerTests
{
    private string _root = null!;

    [BeforeTest]
    public void Setup() => _root = Path.Combine(Path.GetTempPath(), "eplugin-manager-reopen-" + Guid.NewGuid().ToString("N"));

    [AfterTest]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestCase]
    public void SetMarkerIsTakenOnce()
    {
        var marker = new ManagerReopenMarker(Path.Combine(_root, ".godot", "eplugin", "manager-open"));
        Assertions.AssertBool(marker.IsSet).IsFalse();
        Assertions.AssertBool(marker.Take()).IsFalse();

        marker.Set();
        Assertions.AssertBool(marker.IsSet).IsTrue();
        // A second restart renews the marker rather than failing on the existing file.
        marker.Set();
        Assertions.AssertBool(marker.Take()).IsTrue();
        Assertions.AssertBool(File.Exists(marker.Path)).IsFalse();
        Assertions.AssertBool(marker.Take()).IsFalse();
    }

    [TestCase]
    public void ExpiredMarkerIsIgnoredAndRemoved()
    {
        var marker = new ManagerReopenMarker(Path.Combine(_root, "manager-open"));
        marker.Set();
        File.SetLastWriteTimeUtc(marker.Path, DateTime.UtcNow - ManagerReopenMarker.MaxAge - TimeSpan.FromMinutes(1));
        Assertions.AssertBool(marker.IsSet).IsFalse();
        Assertions.AssertBool(marker.Take()).IsFalse();
        Assertions.AssertBool(File.Exists(marker.Path)).IsFalse();
    }
}
