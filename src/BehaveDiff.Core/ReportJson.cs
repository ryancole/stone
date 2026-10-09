using System.Text.Json;
using System.Text.Json.Serialization;

namespace BehaveDiff.Core;

/// <summary>The --json contract: camelCase, enums as strings, nulls kept so the shape is stable.</summary>
public static class ReportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(Report report) => JsonSerializer.Serialize(report, Options);

    public static Report? Deserialize(string json) => JsonSerializer.Deserialize<Report>(json, Options);
}
