using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BehaveDiff.Core.Diffing;

namespace BehaveDiff.Core.Classify;

public sealed record AcceptedDifference(string Id, string Reason, DateTimeOffset AcceptedAt);

/// <summary>.behavediff/accepted.json: differences accepted with a reason, keyed by stable id.</summary>
public static class AcceptedStore
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IReadOnlyList<AcceptedDifference> Load(string path)
    {
        if (!File.Exists(path)) return [];
        return JsonSerializer.Deserialize<List<AcceptedDifference>>(File.ReadAllText(path), Options) ?? [];
    }

    public static void Add(string path, string id, string reason)
    {
        var all = Load(path).Where(a => a.Id != id).Append(new AcceptedDifference(id, reason, DateTimeOffset.UtcNow)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(all, Options));
    }
}

/// <summary>
/// Deterministic v1 classification: --expect rules make a difference Intended, accepted ids make it
/// Accepted, everything else is Unexpected. The reason records which rule fired, so a later
/// classifier (e.g. an LLM) slots in as another reason source without a schema change.
/// </summary>
static class Classifier
{
    public static Difference Classify(DiffItem item, IReadOnlyList<string> expect, IReadOnlyDictionary<string, AcceptedDifference> accepted)
    {
        var id = StableId(item);
        var (classification, reason) =
            accepted.TryGetValue(id, out var acc) ? (Classification.Accepted, new ClassificationReason("accepted", null, acc.Reason))
            : expect.FirstOrDefault(e => Matches(e, item)) is { } rule ? (Classification.Intended, new ClassificationReason("expect", rule, null))
            : (Classification.Unexpected, ClassificationReason.Default);

        return new Difference(id, item.Kind, classification, reason, item.Key.Test, item.Key.Kind, item.Key.Trigger,
            item.Key.Entity, item.Path, item.Before, item.After, item.Key);
    }

    /// <summary>"entity:Widget" matches db-writes of that entity; anything else matches the trigger ("POST /widgets", "GET /widgets/*").</summary>
    internal static bool Matches(string expect, DiffItem item)
    {
        if (expect.StartsWith("entity:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(item.Key.Entity, expect["entity:".Length..].Trim(), StringComparison.OrdinalIgnoreCase);

        var want = NormalizeTrigger(expect);
        var have = NormalizeTrigger(item.Key.Trigger);
        return want.EndsWith('*')
            ? have.StartsWith(want[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(want, have, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collapses whitespace and drops route constraints: "GET /widgets/{id:int}" == "GET /widgets/{id}".</summary>
    static string NormalizeTrigger(string trigger) =>
        Regex.Replace(Regex.Replace(trigger.Trim(), @"\s+", " "), @"\{([^}:]+):[^}]*\}", "{$1}").TrimEnd('/');

    /// <summary>Same change, same id, run after run: hash of what was paired plus where it differs.</summary>
    internal static string StableId(DiffItem item)
    {
        var k = item.Key;
        var text = $"{k.Test}|{k.Kind}|{k.Trigger}|{k.Entity}|{k.Occurrence}|{item.Path}|{item.Kind}";
        return "d-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
    }
}
