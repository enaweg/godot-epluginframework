#if TOOLS
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Enaweg.Plugin.Internal;

/// <summary>Why the plugin state files cannot be read, and what the user can do about it.</summary>
/// <param name="File">The file that failed.</param>
/// <param name="Message">What is wrong with it.</param>
/// <param name="Hint">What to do, ending with the restart that reads it again.</param>
internal sealed record StateFileProblem(string File, string Message, string Hint)
{
    /// <summary>What is wrong with a state file that cannot be read, in words a user can act on.</summary>
    /// <param name="shared">The committed file, rather than the local <c>.user</c> one.</param>
    public static StateFileProblem Describe(string file, bool shared, Exception ex)
    {
        const string restart = " Then restart the editor, which reads it again.";
        string? text = null;
        try { text = System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : null; }
        catch (Exception read) when (read is IOException or UnauthorizedAccessException) { /* the exception below tells */ }

        if (text is not null && HasConflictMarkers(text))
            return new(file, "It contains git merge conflict markers (<<<<<<<, =======, >>>>>>>).",
                "Resolve the conflict: keep the entries of both sides inside \"plugins\" (and \"licenses\", \"welcomes\"), " +
                "each plugin once, and remove the markers." + restart);
        if (ex is PluginStateDocuments.UnsupportedSchemaException schema)
            return new(file, $"It has schemaVersion {(schema.Found == 0 ? "(none)" : schema.Found.ToString(CultureInfo.InvariantCulture))}, " +
                             $"this version of ePlugin reads schemaVersion {PluginStateDocuments.SchemaVersion}.",
                schema.Found > PluginStateDocuments.SchemaVersion
                    ? "A newer ePlugin Framework wrote it: update the framework in addons/ePlugin to the version your team uses." + restart
                    : "Restore the file from version control." + restart);
        if (ex is JsonException json)
            return new(file, $"It is not valid JSON{(json.LineNumber is { } line ? $" (line {line + 1})" : "")}: {json.Message}",
                (shared ? "Restore it from version control or fix the JSON." : "Fix the JSON, or delete the file: it only blocks retries of failed plugin operations on this machine.") + restart);
        if (ex is InvalidDataException)
            return new(file, ex.Message,
                (shared ? "Restore it from version control or fix the entry." : "Fix the entry, or delete the file: it only blocks retries of failed plugin operations on this machine.") + restart);
        if (ex is UnauthorizedAccessException)
            return new(file, $"It cannot be read: {ex.Message}", "Check the file's permissions." + restart);
        return new(file, $"It cannot be read: {ex.Message}",
            "Close programs that may lock it (another editor, a git client) and check its permissions." + restart);
    }

    private static bool HasConflictMarkers(string text) =>
        text.Split('\n').Any(l => l.StartsWith("<<<<<<<", StringComparison.Ordinal) || l.StartsWith(">>>>>>>", StringComparison.Ordinal));
}
#endif
