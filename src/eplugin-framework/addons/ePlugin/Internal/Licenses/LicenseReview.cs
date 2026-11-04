#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace Enaweg.Plugin.Internal.Licenses;

/// <summary>What was decided about a license of a <see cref="LicenseReview"/>.</summary>
/// <remarks>
/// <see cref="Unreadable"/>: the license cannot be shown (<see cref="LicenseEntry.Problem"/>), so it can never be
/// accepted. It is decided from the start and cancels the activations that need it, like a declined license.
/// </remarks>
internal enum LicenseDecision { Pending, Accepted, Declined, Skipped, Unreadable }

/// <summary>
/// A plugin waiting for licenses before it is enabled or updated: its own and those of the dependencies enabled
/// together with it.
/// </summary>
/// <param name="Enabled">
/// The plugin is enabled already, but its license is not accepted as it is now, e.g. after an update changed the
/// license its recipe sets. Declining disables it.
/// </param>
internal sealed record LicenseActivation(string Slug, string Name, IReadOnlyList<string> Licenses, bool Enabled = false);

/// <summary>
/// The licenses shown in one license dialog, with what the user decided for each. Pure, so the dialog only renders it.
/// </summary>
/// <remarks>
/// An activation goes ahead only when every license it needs was accepted. Declining a license cancels the
/// activations that need it, which skips the licenses no remaining activation needs. A license that cannot be read is
/// never accepted, also not by <see cref="AcceptAll"/>: nobody could have read what they agreed to, and acceptance is
/// recorded by where the license comes from, so a file added later would count as accepted without being shown.
/// </remarks>
internal sealed class LicenseReview
{
    private readonly Dictionary<string, LicenseDecision> _decisions = new(StringComparer.Ordinal);

    public LicenseReview(IReadOnlyList<LicenseEntry> entries, IReadOnlyList<LicenseActivation> activations,
        bool isUpdate = false)
    {
        Entries = entries.DistinctBy(e => e.Slug).ToArray();
        Activations = activations;
        IsUpdate = isUpdate;
        foreach (var entry in Entries)
            _decisions[entry.Slug] = entry.Problem is null ? LicenseDecision.Pending : LicenseDecision.Unreadable;
        SkipUnneeded();
    }

    public IReadOnlyList<LicenseEntry> Entries { get; }
    public IReadOnlyList<LicenseActivation> Activations { get; }
    /// <summary>The licenses belong to updated plugins rather than plugins being enabled.</summary>
    public bool IsUpdate { get; }

    public LicenseDecision DecisionOf(string slug) => _decisions.GetValueOrDefault(slug, LicenseDecision.Skipped);
    public bool IsComplete => _decisions.Values.All(d => d != LicenseDecision.Pending);
    public int PendingCount => _decisions.Values.Count(d => d == LicenseDecision.Pending);
    public IEnumerable<LicenseEntry> Accepted => Entries.Where(e => DecisionOf(e.Slug) == LicenseDecision.Accepted);
    /// <summary>The licenses that cannot be read, so they cannot be accepted.</summary>
    public IEnumerable<LicenseEntry> Unreadable => Entries.Where(e => DecisionOf(e.Slug) == LicenseDecision.Unreadable);
    public IEnumerable<LicenseActivation> Approved =>
        Activations.Where(a => a.Licenses.All(l => DecisionOf(l) == LicenseDecision.Accepted));
    public IEnumerable<LicenseActivation> Canceled =>
        Activations.Where(a => a.Licenses.Any(l => DecisionOf(l) is LicenseDecision.Declined or LicenseDecision.Skipped or LicenseDecision.Unreadable));

    /// <summary>The licenses of another review are added; a license already listed keeps its decision.</summary>
    public LicenseReview Merge(LicenseReview other)
    {
        var merged = new LicenseReview(Entries.Concat(other.Entries).ToArray(),
            Activations.Concat(other.Activations.Where(a => Activations.All(o => o.Slug != a.Slug))).ToArray(),
            IsUpdate && other.IsUpdate);
        foreach (var (slug, decision) in _decisions) merged._decisions[slug] = decision;
        merged.SkipUnneeded();
        return merged;
    }

    public void Accept(string slug) => Decide(slug, LicenseDecision.Accepted);

    public void Decline(string slug)
    {
        Decide(slug, LicenseDecision.Declined);
        SkipUnneeded();
    }

    /// <summary>Accepts every license not decided yet; one that cannot be read stays unaccepted.</summary>
    public void AcceptAll()
    {
        foreach (var entry in Entries) Decide(entry.Slug, LicenseDecision.Accepted);
    }

    /// <summary>Declines every license not decided yet, e.g. when the dialog is closed.</summary>
    public void DeclineRemaining()
    {
        foreach (var entry in Entries) Decide(entry.Slug, LicenseDecision.Declined);
    }

    /// <summary>The next license still waiting for a decision, searching from after <paramref name="slug"/>.</summary>
    public LicenseEntry? NextPending(string? slug = null)
    {
        var start = slug is null ? 0 : Math.Max(0, Entries.ToList().FindIndex(e => e.Slug == slug) + 1);
        return Entries.Skip(start).Concat(Entries.Take(start)).FirstOrDefault(e => DecisionOf(e.Slug) == LicenseDecision.Pending);
    }

    /// <summary>The other plugins whose activation needs this license, e.g. those that depend on its plugin.</summary>
    public IEnumerable<LicenseActivation> NeededBy(string slug) =>
        Activations.Where(a => a.Slug != slug && a.Licenses.Contains(slug));

    /// <summary>
    /// Why a canceled activation does not go ahead, to follow "… was not enabled: ". A license that cannot be read
    /// comes first, as only fixing it helps.
    /// </summary>
    public string CanceledReason(LicenseActivation activation)
    {
        var needed = Entries.Where(e => activation.Licenses.Contains(e.Slug)).ToArray();
        if (DescribeUnreadable(needed.Where(e => DecisionOf(e.Slug) == LicenseDecision.Unreadable)) is { } unreadable)
            return unreadable;
        var declined = needed.Where(e => DecisionOf(e.Slug) == LicenseDecision.Declined).ToArray();
        return declined.Length > 0
            ? $"the license of {string.Join(", ", declined.Select(e => e.Name))} was declined"
            : "a license it needs was not accepted";
    }

    /// <summary>Why the licenses that cannot be read cannot be accepted, to follow "… was not enabled: "; null without any.</summary>
    public string? UnreadableReason => DescribeUnreadable(Unreadable);

    private static string? DescribeUnreadable(IEnumerable<LicenseEntry> unreadable)
    {
        var text = string.Join("; ", unreadable.Select(e => $"the license of {e.Name} cannot be read, so it cannot be accepted ({e.Problem!.TrimEnd('.')})"));
        return text.Length == 0 ? null : text;
    }

    private void Decide(string slug, LicenseDecision decision)
    {
        if (_decisions.TryGetValue(slug, out var current) && current == LicenseDecision.Pending) _decisions[slug] = decision;
    }

    /// <summary>A license only canceled activations still need is not asked about anymore.</summary>
    private void SkipUnneeded()
    {
        foreach (var entry in Entries.Where(e => DecisionOf(e.Slug) == LicenseDecision.Pending).ToArray())
        {
            var needed = Activations.Where(a => a.Licenses.Contains(entry.Slug)).ToArray();
            if (needed.Length > 0 && needed.All(a => a.Licenses.Any(l => DecisionOf(l) is LicenseDecision.Declined or LicenseDecision.Unreadable)))
                _decisions[entry.Slug] = LicenseDecision.Skipped;
        }
    }
}
#endif
