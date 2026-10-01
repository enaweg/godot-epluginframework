using System;
using System.IO;
using GdUnit4;

namespace Enaweg.Plugin.Tests;

[TestSuite]
[RequireGodotRuntime]
public class UpdateMetadataTests
{
    [TestCase]
    public void OptionalUpdateUrlIsReadWithoutMissingKeyErrors()
    {
        var path = Path.Combine(Path.GetTempPath(), "plugin-" + Guid.NewGuid().ToString("N") + ".cfg");
        try
        {
            File.WriteAllText(path, "[plugin]\nname=\"Test\"\nversion=\"1.0\"\nscript=\"plugin.gd\"\n");
            Assertions.AssertObject(EditorPluginExtensions.ReadMetadata(path)!.UpdateUrl).IsNull();
            File.AppendAllText(path, "update_url=\"https://github.com/owner/repo/releases\"\n");
            Assertions.AssertString(EditorPluginExtensions.ReadMetadata(path)!.UpdateUrl!).IsEqual("https://github.com/owner/repo/releases");
        }
        finally { File.Delete(path); }
    }
}
