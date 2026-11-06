#if TOOLS
using System.IO;

namespace Enaweg.Plugin.Internal.Update;

/// <summary>
/// Where the updater keeps its files. Early recovery reads them in another framework version than the one that wrote
/// them, so they never move.
/// </summary>
internal static class UpdatePaths
{
    /// <summary>The folder of the transaction folders, relative to the project root.</summary>
    public const string Transactions = ".godot/eplugin/updates";

    /// <summary>The update cache (<see cref="UpdateStateStore"/>), relative to the project root.</summary>
    public const string Cache = ".godot/eplugin/update-state.json";

    /// <summary>A transaction folder's journal; a folder without one holds only a staged batch.</summary>
    public const string Journal = "journal.json";

    /// <summary>Written into a self-update's transaction folder once the new framework was verified.</summary>
    public const string HealthyMarker = "healthy.marker";

    public static string TransactionsOf(string projectRoot) => Path.Combine(projectRoot, Transactions);

    public static string CacheOf(string projectRoot) => Path.Combine(projectRoot, Cache);

    public static string JournalIn(string transactionDirectory) => Path.Combine(transactionDirectory, Journal);
}
#endif
