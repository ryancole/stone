using System.Text.Json;
using System.Text.Json.Nodes;

namespace BehaveDiff.Core.Diffing;

/// <summary>
/// Row ids by insert order. One database serves a whole run, so identity values depend on
/// everything inserted earlier; "the 3rd Widget inserted" is stable across runs, "Widget 7" isn't.
/// Built from the run's Added db-writes with a single integer key, in seq order.
/// </summary>
sealed class IdentityMap
{
    readonly Dictionary<(string Entity, long Id), int> _ordinals = new();
    readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    public static IdentityMap Build(Recording recording)
    {
        var map = new IdentityMap();
        foreach (var o in recording.Observations.OrderBy(o => o.Seq))
        {
            if (o.Kind != ObservationKinds.DbWrite || o.Data is not JsonObject data) continue;
            if ((string?)data["state"] != "Added" || (string?)data["outcome"] is not null) continue;
            if (data["keys"] is not JsonArray { Count: 1 } keys) continue;
            var entity = (string?)data["entity"];
            var keyName = (string?)keys[0];
            if (entity is null || keyName is null) continue;
            if (TryGetInteger(data["values"]?[keyName], out var id) && !map._ordinals.ContainsKey((entity, id)))
            {
                var n = map._counts[entity] = map._counts.GetValueOrDefault(entity) + 1;
                map._ordinals[(entity, id)] = n;
            }
        }
        return map;
    }

    public int? Ordinal(string entity, long id) => _ordinals.TryGetValue((entity, id), out var n) ? n : null;

    public IEnumerable<string> Entities => _counts.Keys;

    /// <summary>
    /// Same row by insert order. With <paramref name="entity"/>: that entity's ordinals must match.
    /// Without (e.g. an "id" field in a response): some entity must give both the same ordinal.
    /// </summary>
    public static bool SameRow(IdentityMap baseMap, IdentityMap currentMap, string? entity, long baseId, long currentId)
    {
        if (entity is not null)
            return baseMap.Ordinal(entity, baseId) is { } b && currentMap.Ordinal(entity, currentId) == b;
        foreach (var e in baseMap.Entities)
        {
            if (baseMap.Ordinal(e, baseId) is { } b && currentMap.Ordinal(e, currentId) == b)
                return true;
        }
        return false;
    }

    public static bool TryGetInteger(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => v.TryGetValue(out value),
            JsonValueKind.String => long.TryParse(v.GetValue<string>(), out value),
            _ => false,
        };
    }
}
