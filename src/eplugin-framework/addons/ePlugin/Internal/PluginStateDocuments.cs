#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enaweg.Plugin.Internal;

/// <summary>
/// The JSON form of the plugin state files <see cref="PluginStateStore"/> reads and writes: the documents, how they are
/// read and validated, and how they are written. See the store's remarks for what the files hold and how they stay
/// compatible.
/// </summary>
internal static class PluginStateDocuments
{
    public const int SchemaVersion = 1;

    // allowIntegerValues: false makes a reader fail on a state it does not know, see PluginStateStore's remarks
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>The shared document of a project without a shared file.</summary>
    public static SharedDocument EmptyShared() => new() { SchemaVersion = SchemaVersion, Plugins = [] };

    /// <summary>The local document of a project without a local file.</summary>
    public static LocalDocument EmptyLocal() => new() { SchemaVersion = SchemaVersion, Attempts = [] };

    /// <summary>Reads and validates a shared file, with the entries of its nested lists in the others.</summary>
    /// <exception cref="JsonException">It is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">An entry or list is invalid.</exception>
    /// <exception cref="UnsupportedSchemaException">It has another schema version.</exception>
    public static SharedDocument ReadShared(byte[] bytes)
    {
        var shared = JsonSerializer.Deserialize<SharedDocument>(bytes, JsonOptions)?.Unnested()
                     ?? throw new InvalidDataException("The file contains no plugin state.");
        ValidateShared(shared);
        return shared;
    }

    /// <summary>Reads and validates a local file, with its nested attempts in the others.</summary>
    /// <exception cref="JsonException">It is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">An entry or list is invalid.</exception>
    /// <exception cref="UnsupportedSchemaException">It has another schema version.</exception>
    public static LocalDocument ReadLocal(byte[] bytes)
    {
        var local = JsonSerializer.Deserialize<LocalDocument>(bytes, JsonOptions)?.Unnested()
                    ?? throw new InvalidDataException("The file contains no plugin state.");
        ValidateLocal(local);
        return local;
    }

    /// <summary>The shared file of these entries, each list ordered by slug.</summary>
    public static byte[] WriteShared(IEnumerable<SharedPluginState> states, Guid? completedAttemptId,
        IEnumerable<AcceptedLicense> licenses, IEnumerable<ShownWelcome> welcomes)
    {
        var (plugins, nestedPlugins) = Split(states.OrderBy(x => x.Slug, StringComparer.Ordinal)
            .Select(SharedEntry.From), x => x.Slug);
        var (licenseEntries, nestedLicenses) = Split(licenses.OrderBy(x => x.Slug, StringComparer.Ordinal)
            .Select(LicenseDocumentEntry.From), x => x.Slug);
        var (welcomeEntries, nestedWelcomes) = Split(welcomes.OrderBy(x => x.Slug, StringComparer.Ordinal)
            .Select(WelcomeDocumentEntry.From), x => x.Slug);
        return Serialize(new SharedDocument
        {
            SchemaVersion = SchemaVersion,
            LastCompletedAttemptId = completedAttemptId,
            Plugins = plugins ?? [],
            Licenses = licenseEntries,
            Welcomes = welcomeEntries,
            NestedPlugins = nestedPlugins,
            NestedLicenses = nestedLicenses,
            NestedWelcomes = nestedWelcomes
        });
    }

    /// <summary>The local file of these attempts, ordered by slug.</summary>
    public static byte[] WriteLocal(IEnumerable<LocalPluginAttempt> attempts)
    {
        var (entries, nested) = Split(attempts.OrderBy(x => x.Slug, StringComparer.Ordinal).Select(LocalEntry.From), x => x.Slug);
        return Serialize(new LocalDocument { SchemaVersion = SchemaVersion, Attempts = entries ?? [], NestedAttempts = nested });
    }

    public static bool IsCompleted(PersistedPluginState state) =>
        state is PersistedPluginState.Activated or PersistedPluginState.Deactivated;

    // A plugin in a subfolder has a slug with '/'; see PluginStateStore's remarks for where its entries are written.
    public static bool ValidSlug(string? slug) => AddonLayout.IsStoredSlug(slug);

    private static byte[] Serialize<T>(T document) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, JsonOptions) + "\n");

    private static void ValidateLocal(LocalDocument local)
    {
        if (local.SchemaVersion != SchemaVersion)
        {
            throw new UnsupportedSchemaException(local.SchemaVersion);
        }

        if (local.Attempts is null)
        {
            throw new InvalidDataException("It has no \"attempts\" list.");
        }

        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in local.Attempts)
        {
            if (entry.AttemptId is null || entry.AttemptId == Guid.Empty ||
                !ValidSlug(entry.Slug) || entry.TargetState is null || !IsCompleted(entry.TargetState.Value) ||
                entry.State is not (PersistedPluginState.Failed or PersistedPluginState.Invalid) ||
                string.IsNullOrWhiteSpace(entry.Reason) || !slugs.Add(entry.Slug!))
            {
                throw new InvalidDataException($"It has an invalid or duplicate attempt{(ValidSlug(entry.Slug) ? $" for {entry.Slug}" : "")}.");
            }
        }
    }

    private static void ValidateShared(SharedDocument shared)
    {
        if (shared.SchemaVersion != SchemaVersion)
        {
            throw new UnsupportedSchemaException(shared.SchemaVersion);
        }

        if (shared.Plugins is null)
        {
            throw new InvalidDataException("It has no \"plugins\" list.");
        }

        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in shared.Plugins)
        {
            if (!ValidSlug(entry.Slug) || string.IsNullOrWhiteSpace(entry.Version) ||
                entry.State is null || !IsCompleted(entry.State.Value) || !slugs.Add(entry.Slug!))
            {
                throw new InvalidDataException($"It has an invalid or duplicate entry in \"plugins\"{(ValidSlug(entry.Slug) ? $" for {entry.Slug}" : "")}.");
            }
        }

        slugs.Clear();
        foreach (var entry in shared.Licenses ?? [])
        {
            if (!ValidSlug(entry.Slug) || string.IsNullOrWhiteSpace(entry.License) || !slugs.Add(entry.Slug!))
            {
                throw new InvalidDataException($"It has an invalid or duplicate entry in \"licenses\"{(ValidSlug(entry.Slug) ? $" for {entry.Slug}" : "")}.");
            }
        }

        slugs.Clear();
        foreach (var entry in shared.Welcomes ?? [])
        {
            if (!ValidSlug(entry.Slug) || !slugs.Add(entry.Slug!))
            {
                throw new InvalidDataException($"It has an invalid or duplicate entry in \"welcomes\"{(ValidSlug(entry.Slug) ? $" for {entry.Slug}" : "")}.");
            }
        }
    }

    /// <summary>
    /// Splits entries into those of plugins directly below the addons folder and those of plugins in a subfolder, which
    /// are written to the nested lists; null for an empty list, which is left out.
    /// </summary>
    private static (List<T>? Top, List<T>? Nested) Split<T>(IEnumerable<T> entries, Func<T, string?> slug)
    {
        var all = entries.ToList();
        var top = all.Where(e => !AddonLayout.IsNested(slug(e) ?? "")).ToList();
        var nested = all.Where(e => AddonLayout.IsNested(slug(e) ?? "")).ToList();
        return (top.Count > 0 ? top : null, nested.Count > 0 ? nested : null);
    }

    private static List<T>? Join<T>(List<T>? top, List<T>? nested) => nested is null ? top : [.. top ?? [], .. nested];

    /// <summary>A state file of a schema this version cannot read.</summary>
    internal sealed class UnsupportedSchemaException(int found) : IOException("Unsupported plugin state schema.")
    {
        public int Found { get; } = found;
    }

    internal sealed class SharedDocument
    {
        public int SchemaVersion { get; set; }
        public Guid? LastCompletedAttemptId { get; set; }
        // required: a misspelled key must not read as no plugins
        public List<SharedEntry>? Plugins { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<LicenseDocumentEntry>? Licenses { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<WelcomeDocumentEntry>? Welcomes { get; set; }

        // The entries of plugins in a subfolder, which older versions skip; see PluginStateStore's remarks.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SharedEntry>? NestedPlugins { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<LicenseDocumentEntry>? NestedLicenses { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<WelcomeDocumentEntry>? NestedWelcomes { get; set; }

        /// <summary>The document with the entries of the nested lists in the others, as this version reads them.</summary>
        public SharedDocument Unnested() => new()
        {
            SchemaVersion = SchemaVersion, LastCompletedAttemptId = LastCompletedAttemptId,
            // a file without "plugins" stays unreadable, whatever its nested list holds
            Plugins = Plugins is null ? null : Join(Plugins, NestedPlugins),
            Licenses = Join(Licenses, NestedLicenses), Welcomes = Join(Welcomes, NestedWelcomes)
        };
    }

    internal sealed class WelcomeDocumentEntry
    {
        public string? Slug { get; set; }
        public string? Version { get; set; }
        public DateTimeOffset? ShownUtc { get; set; }

        public static WelcomeDocumentEntry From(ShownWelcome welcome) => new()
        {
            Slug = welcome.Slug, Version = welcome.Version, ShownUtc = welcome.ShownUtc
        };

        /// <summary>The validated entry as the store holds it.</summary>
        public ShownWelcome ToWelcome() => new(Slug!, Version ?? "", ShownUtc ?? default);
    }

    internal sealed class LicenseDocumentEntry
    {
        public string? Slug { get; set; }
        public string? License { get; set; }
        public string? Version { get; set; }
        public DateTimeOffset? AcceptedUtc { get; set; }
        public bool Automatic { get; set; }

        public static LicenseDocumentEntry From(AcceptedLicense license) => new()
        {
            Slug = license.Slug, License = license.License, Version = license.Version, AcceptedUtc = license.AcceptedUtc,
            Automatic = license.Automatic
        };

        /// <summary>The validated entry as the store holds it.</summary>
        public AcceptedLicense ToLicense() => new(Slug!, License!, Version ?? "", AcceptedUtc ?? default, Automatic);
    }

    internal sealed class SharedEntry
    {
        public string? Slug { get; set; }
        public string? Version { get; set; }
        public PersistedPluginState? State { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Missing { get; set; }

        public static SharedEntry From(SharedPluginState state) => new()
        {
            Slug = state.Slug, Version = state.Version, State = state.State, Missing = state.Missing
        };

        /// <summary>The validated entry as the store holds it.</summary>
        public SharedPluginState ToState() => new(Slug!, Version!, State!.Value, Missing);
    }

    internal sealed class LocalDocument
    {
        public int SchemaVersion { get; set; }
        public List<LocalEntry>? Attempts { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<LocalEntry>? NestedAttempts { get; set; }

        /// <summary>The document with the nested attempts in <see cref="Attempts"/>, as this version reads them.</summary>
        public LocalDocument Unnested() => new()
        {
            SchemaVersion = SchemaVersion, Attempts = Attempts is null ? null : Join(Attempts, NestedAttempts)
        };
    }

    internal sealed class LocalEntry
    {
        public Guid? AttemptId { get; set; }
        public string? Slug { get; set; }
        public string? InstalledVersion { get; set; }
        public PersistedPluginState? TargetState { get; set; }
        public PersistedPluginState? State { get; set; }
        public string? Reason { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Detail { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Log { get; set; }

        public static LocalEntry From(LocalPluginAttempt attempt) => new()
        {
            AttemptId = attempt.AttemptId,
            Slug = attempt.Slug,
            InstalledVersion = attempt.InstalledVersion,
            TargetState = attempt.TargetState,
            State = attempt.State,
            Reason = attempt.Reason,
            Detail = attempt.Detail,
            Log = attempt.Log
        };

        /// <summary>The validated entry as the store holds it.</summary>
        public LocalPluginAttempt ToAttempt() =>
            new(AttemptId!.Value, Slug!, InstalledVersion, TargetState!.Value, State!.Value, Reason!, Detail, Log);
    }
}
#endif
