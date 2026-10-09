using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BehaveDiff.Core.Diffing;

/// <summary>
/// Replaces values that differ run-to-run by construction (GUIDs, timestamps, trace ids) with
/// placeholders and drops configured headers/columns. Generic over observation data.
/// </summary>
sealed class Normalizer(BehaveDiffConfig.IgnoreConfig ignore)
{
    // Order matters: trace ids contain a 32-hex run that the GUID-N pattern would also match.
    static readonly Regex TraceId = new(@"\b00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex GuidD = new(@"(?<![0-9a-fA-F])[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?![0-9a-fA-F])", RegexOptions.Compiled);
    static readonly Regex GuidN = new(@"(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", RegexOptions.Compiled);
    static readonly Regex Timestamp = new(@"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}:\d{2})?", RegexOptions.Compiled);

    public const string GuidToken = "<guid>";
    public const string TimestampToken = "<timestamp>";
    public const string TraceIdToken = "<traceid>";

    readonly HashSet<string> _headers = new(ignore.Headers, StringComparer.OrdinalIgnoreCase);

    public Recording Normalize(Recording recording) =>
        recording with { Observations = recording.Observations.Select(Normalize).ToList() };

    public Observation Normalize(Observation o)
    {
        var data = o.Data?.DeepClone();
        if (data is JsonObject obj)
        {
            if (o.Kind == ObservationKinds.HttpResponse) DropHeaders(obj);
            if (o.Kind == ObservationKinds.DbWrite) DropColumns(obj);
        }
        return o with { Data = Scrub(data), Trigger = ScrubString(o.Trigger) };
    }

    void DropHeaders(JsonObject data)
    {
        if (data["headers"] is not JsonObject headers) return;
        var hasBody = data["body"] is not null;
        foreach (var name in headers.Select(h => h.Key).ToList())
        {
            // Content-Length just mirrors the body, which is compared directly.
            if (_headers.Contains(name) || (hasBody && name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
                headers.Remove(name);
        }
    }

    void DropColumns(JsonObject data)
    {
        if (ignore.DbColumns.Count == 0) return;
        var entity = (string?)data["entity"];
        foreach (var section in new[] { "values", "providerValues" })
        {
            if (data[section] is not JsonObject values) continue;
            foreach (var column in values.Select(v => v.Key).ToList())
            {
                if (ignore.DbColumns.Any(rule =>
                        rule.Equals(column, StringComparison.OrdinalIgnoreCase)
                        || rule.Equals($"{entity}.{column}", StringComparison.OrdinalIgnoreCase)))
                    values.Remove(column);
            }
        }
    }

    static JsonNode? Scrub(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                    obj[key] = Scrub(obj[key]?.DeepClone());
                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    arr[i] = Scrub(arr[i]?.DeepClone());
                return arr;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var s = value.GetValue<string>();
                var scrubbed = ScrubString(s);
                return ReferenceEquals(s, scrubbed) ? value : JsonValue.Create(scrubbed);
            default:
                return node;
        }
    }

    internal static string ScrubString(string s)
    {
        if (s.Length < 10) return s;
        var r = TraceId.Replace(s, TraceIdToken);
        r = GuidD.Replace(r, GuidToken);
        r = GuidN.Replace(r, GuidToken);
        r = Timestamp.Replace(r, TimestampToken);
        return r == s ? s : r;
    }
}
