using System.Text.Json;
using System.Text.Json.Nodes;

namespace BehaveDiff.Core.Diffing;

enum IdentityScopeKind { None, Entity, AnyEntity, PathSegments }

/// <summary>How to compare integers at a path: as row ids of an entity, of any entity, or not at all.</summary>
readonly record struct IdentityScope(IdentityScopeKind Kind, string? Entity = null)
{
    public static readonly IdentityScope None = new(IdentityScopeKind.None);
}

sealed record RawDiff(DifferenceKind Kind, string Path, JsonNode? Before, JsonNode? After);

/// <summary>Structural JSON comparison. Paths look like $.body.items[0].name.</summary>
sealed class JsonDiff(IdentityMap baseIds, IdentityMap currentIds, Func<IReadOnlyList<string>, IdentityScope> scopeOf)
{
    public List<RawDiff> Compare(JsonNode? before, JsonNode? after)
    {
        var results = new List<RawDiff>();
        Compare(before, after, "$", [], results);
        return results;
    }

    public bool AreEqual(JsonNode? before, JsonNode? after) => Compare(before, after).Count == 0;

    void Compare(JsonNode? a, JsonNode? b, string path, List<string> segments, List<RawDiff> results)
    {
        var ka = KindOf(a);
        var kb = KindOf(b);

        if (ka == JsonValueKind.Object && kb == JsonValueKind.Object)
        {
            var oa = a!.AsObject();
            var ob = b!.AsObject();
            foreach (var key in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)).OrderBy(k => k, StringComparer.Ordinal))
            {
                var childPath = Child(path, key);
                var inA = oa.TryGetPropertyValue(key, out var va);
                var inB = ob.TryGetPropertyValue(key, out var vb);
                if (!inA) results.Add(new(DifferenceKind.FieldAdded, childPath, null, vb?.DeepClone()));
                else if (!inB) results.Add(new(DifferenceKind.FieldRemoved, childPath, va?.DeepClone(), null));
                else
                {
                    segments.Add(key);
                    Compare(va, vb, childPath, segments, results);
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            return;
        }

        if (ka == JsonValueKind.Array && kb == JsonValueKind.Array)
        {
            var aa = a!.AsArray();
            var ab = b!.AsArray();
            for (var i = 0; i < Math.Max(aa.Count, ab.Count); i++)
            {
                var childPath = $"{path}[{i}]";
                if (i >= aa.Count) results.Add(new(DifferenceKind.FieldAdded, childPath, null, ab[i]?.DeepClone()));
                else if (i >= ab.Count) results.Add(new(DifferenceKind.FieldRemoved, childPath, aa[i]?.DeepClone(), null));
                else
                {
                    segments.Add($"[{i}]");
                    Compare(aa[i], ab[i], childPath, segments, results);
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            return;
        }

        if (JsonNode.DeepEquals(a, b) || SameByIdentity(a, b, segments)) return;

        var structural = ka is JsonValueKind.Object or JsonValueKind.Array || kb is JsonValueKind.Object or JsonValueKind.Array;
        var typeChanged = ka != JsonValueKind.Null && kb != JsonValueKind.Null && Family(ka) != Family(kb);
        results.Add(new(structural || typeChanged ? DifferenceKind.TypeChanged : DifferenceKind.ValueChanged, path, a?.DeepClone(), b?.DeepClone()));
    }

    bool SameByIdentity(JsonNode? a, JsonNode? b, List<string> segments)
    {
        var scope = scopeOf(segments);
        switch (scope.Kind)
        {
            case IdentityScopeKind.Entity or IdentityScopeKind.AnyEntity:
                return IdentityMap.TryGetInteger(a, out var x) && IdentityMap.TryGetInteger(b, out var y)
                    && IdentityMap.SameRow(baseIds, currentIds, scope.Entity, x, y);
            case IdentityScopeKind.PathSegments:
                if (a is not JsonValue va || b is not JsonValue vb
                    || va.GetValueKind() != JsonValueKind.String || vb.GetValueKind() != JsonValueKind.String) return false;
                var pa = va.GetValue<string>().Split('/');
                var pb = vb.GetValue<string>().Split('/');
                if (pa.Length != pb.Length) return false;
                for (var i = 0; i < pa.Length; i++)
                {
                    if (pa[i] == pb[i]) continue;
                    if (!long.TryParse(pa[i], out var sa) || !long.TryParse(pb[i], out var sb)
                        || !IdentityMap.SameRow(baseIds, currentIds, null, sa, sb)) return false;
                }
                return true;
            default:
                return false;
        }
    }

    static JsonValueKind KindOf(JsonNode? node) => node switch
    {
        null => JsonValueKind.Null,
        JsonObject => JsonValueKind.Object,
        JsonArray => JsonValueKind.Array,
        _ => node.GetValueKind(),
    };

    static JsonValueKind Family(JsonValueKind kind) => kind == JsonValueKind.False ? JsonValueKind.True : kind;

    static string Child(string path, string key) =>
        key.Length > 0 && key.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '$')
            ? $"{path}.{key}"
            : $"{path}['{key.Replace("'", "\\'")}']";
}
