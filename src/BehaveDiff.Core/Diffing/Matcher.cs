using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BehaveDiff.Core.Diffing;

/// <summary>One difference before filtering/classification, with both sides for explain_difference.</summary>
sealed record DiffItem(MatchKey Key, DifferenceKind Kind, string? Path, JsonNode? Before, JsonNode? After, Observation? Base, Observation? Current);

sealed record MatchResult(
    IReadOnlyList<DiffItem> Diffs,
    IReadOnlyList<MatchKey> PairedKeys,
    IReadOnlyList<MovedObservation> Moved,
    IReadOnlyList<LatencyFlag> Latency);

/// <summary>
/// Pairs observations of two normalized recordings and diffs each pair.
/// Groups by (test, kind, trigger, entity); within a group, pairs by sequence alignment (equal
/// observations anchor, the gaps between anchors pair in order); leftovers are one-sided.
/// Identical one-sided leftovers in different tests are re-paired as moved (lazy fixtures).
/// </summary>
sealed class Matcher(BehaveDiffConfig.IdentityConfig identity, double latencyRegressionPct)
{
    /// <summary>Latency noise floor: a regression must also add at least this much.</summary>
    const double MinLatencyDeltaMs = 20;

    readonly Regex _idField = new(identity.FieldPattern, RegexOptions.CultureInvariant);

    public MatchResult Match(Recording baseRec, Recording currentRec)
    {
        var baseIds = identity.Enabled ? IdentityMap.Build(baseRec) : new IdentityMap();
        var currentIds = identity.Enabled ? IdentityMap.Build(currentRec) : new IdentityMap();

        var diffs = new List<DiffItem>();
        var paired = new List<MatchKey>();
        var latency = new List<LatencyFlag>();
        var removed = new List<(MatchKey Key, Observation Obs)>();
        var added = new List<(MatchKey Key, Observation Obs)>();

        var baseGroups = Group(baseRec.Observations);
        var currentGroups = Group(currentRec.Observations);
        var keys = baseGroups.Keys.Union(currentGroups.Keys)
            .OrderBy(k => Math.Min(FirstSeq(baseGroups, k), FirstSeq(currentGroups, k)))
            .ToList();

        foreach (var g in keys)
        {
            var bs = baseGroups.GetValueOrDefault(g) ?? [];
            var cs = currentGroups.GetValueOrDefault(g) ?? [];
            foreach (var (bi, ci) in Align(bs, cs, (b, c) => DiffFor(b, baseIds, currentIds).AreEqual(b.Data, c.Data)))
            {
                if (bi is { } i && ci is { } j)
                {
                    var key = new MatchKey(g.Test, g.Kind, g.Trigger, g.Entity, i);
                    paired.Add(key);
                    foreach (var d in DiffFor(bs[i], baseIds, currentIds).Compare(bs[i].Data, cs[j].Data))
                        diffs.Add(new DiffItem(key, d.Kind, d.Path, d.Before, d.After, bs[i], cs[j]));
                    if (Latency(key, bs[i], cs[j]) is { } flag) latency.Add(flag);
                }
                else if (bi is { } r) removed.Add((new MatchKey(g.Test, g.Kind, g.Trigger, g.Entity, r), bs[r]));
                else if (ci is { } a) added.Add((new MatchKey(g.Test, g.Kind, g.Trigger, g.Entity, a), cs[a]));
            }
        }

        // Cross-test re-pairing: the same observation, now produced inside a different test.
        var moved = new List<MovedObservation>();
        foreach (var r in removed.ToList())
        {
            var hit = added.FirstOrDefault(a =>
                a.Key.Test != r.Key.Test && a.Key.Kind == r.Key.Kind && a.Key.Trigger == r.Key.Trigger && a.Key.Entity == r.Key.Entity
                && DiffFor(r.Obs, baseIds, currentIds).AreEqual(r.Obs.Data, a.Obs.Data));
            if (hit.Obs is null) continue;
            moved.Add(new MovedObservation(r.Key, hit.Key));
            removed.Remove(r);
            added.Remove(hit);
        }

        foreach (var (key, obs) in removed)
            diffs.Add(new DiffItem(key, DifferenceKind.ObservationRemoved, null, obs.Data?.DeepClone(), null, obs, null));
        foreach (var (key, obs) in added)
            diffs.Add(new DiffItem(key, DifferenceKind.ObservationAdded, null, null, obs.Data?.DeepClone(), null, obs));

        return new MatchResult(diffs, paired, moved, latency);
    }

    readonly record struct GroupKey(string Test, string Kind, string Trigger, string? Entity);

    static Dictionary<GroupKey, List<Observation>> Group(IEnumerable<Observation> observations) =>
        observations.OrderBy(o => o.Seq)
            .GroupBy(o => new GroupKey(o.Test, o.Kind, o.Trigger, o.Entity))
            .ToDictionary(g => g.Key, g => g.ToList());

    static long FirstSeq(Dictionary<GroupKey, List<Observation>> groups, GroupKey key) =>
        groups.TryGetValue(key, out var list) ? list[0].Seq : long.MaxValue;

    /// <summary>
    /// LCS on equality gives anchors; between consecutive anchors the remaining items pair in order;
    /// whatever is left over is one-sided. Equal counts with no anchors degrade to index pairing.
    /// </summary>
    internal static IEnumerable<(int? Base, int? Current)> Align<T>(IReadOnlyList<T> bs, IReadOnlyList<T> cs, Func<T, T, bool> equal)
    {
        var n = bs.Count;
        var m = cs.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = equal(bs[i], cs[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var anchors = new List<(int, int)>();
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (equal(bs[i], cs[j]) && lcs[i, j] == lcs[i + 1, j + 1] + 1) { anchors.Add((i, j)); i++; j++; }
            else if (lcs[i + 1, j] >= lcs[i, j + 1]) i++;
            else j++;
        }
        anchors.Add((n, m)); // sentinel

        var (pb, pc) = (0, 0);
        foreach (var (ab, ac) in anchors)
        {
            var gap = Math.Min(ab - pb, ac - pc);
            for (var k = 0; k < gap; k++) yield return (pb + k, pc + k);
            for (var k = pb + gap; k < ab; k++) yield return (k, null);
            for (var k = pc + gap; k < ac; k++) yield return (null, k);
            if (ab < n && ac < m) yield return (ab, ac);
            (pb, pc) = (ab + 1, ac + 1);
        }
    }

    JsonDiff DiffFor(Observation o, IdentityMap baseIds, IdentityMap currentIds) =>
        new(baseIds, currentIds, identity.Enabled ? segments => ScopeOf(o, segments) : _ => IdentityScope.None);

    IdentityScope ScopeOf(Observation o, IReadOnlyList<string> segments)
    {
        if (o.Kind == ObservationKinds.DbWrite)
        {
            // $.values.<Column> or $.values.<Column>.original/current
            if (segments.Count < 2 || segments[0] != "values" || o.Data is not JsonObject data) return IdentityScope.None;
            var column = segments[1];
            if (data["keys"] is JsonArray keys && keys.Any(k => (string?)k == column))
                return new(IdentityScopeKind.Entity, o.Entity);
            if (data["foreignKeys"]?[column] is JsonValue principal)
                return new(IdentityScopeKind.Entity, (string?)principal);
            return IdentityScope.None;
        }

        if (segments is ["request", "path"]) return new(IdentityScopeKind.PathSegments);
        var name = segments.LastOrDefault(s => !s.StartsWith('['));
        return name is not null && _idField.IsMatch(name) ? new(IdentityScopeKind.AnyEntity) : IdentityScope.None;
    }

    LatencyFlag? Latency(MatchKey key, Observation b, Observation c)
    {
        if (b.Kind == ObservationKinds.TestResult || b.DurationMs is not { } bm || c.DurationMs is not { } cm || bm <= 0) return null;
        var pct = (cm - bm) / bm * 100;
        return pct > latencyRegressionPct && cm - bm >= MinLatencyDeltaMs
            ? new LatencyFlag(key, Math.Round(bm, 1), Math.Round(cm, 1), Math.Round(pct))
            : null;
    }
}
