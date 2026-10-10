#if TOOLS
using System;
using System.IO;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>
/// Remembers across editor restarts that the ePlugin Manager was open, so it opens again once the restarts are done.
/// An update may restart the editor twice (interim and final build); the marker stays until a start that requests no
/// further restart takes it.
/// </summary>
internal sealed class ManagerReopenMarker(string path)
{
    /// <summary>A marker this old belongs to a restart that never happened, e.g. the editor was closed instead.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    public string Path { get; } = path;

    public bool IsSet
    {
        get
        {
            try { return File.Exists(Path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(Path) < MaxAge; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Writes the marker, or renews it so it outlives the next restart.</summary>
    public void Set()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, DateTimeOffset.UtcNow.ToString("O"));
    }

    /// <summary>Removes the marker and returns whether it was set.</summary>
    public bool Take()
    {
        var set = IsSet;
        try { File.Delete(Path); }
        catch (Exception) { /* a marker that cannot be removed expires on its own */ }
        return set;
    }
}
#endif
