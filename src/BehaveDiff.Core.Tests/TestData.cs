using System.Text.Json.Nodes;
using BehaveDiff.Core;

namespace BehaveDiff.Core.Tests;

static class TestData
{
    static long _seq;

    public static Observation Http(string test, string trigger, string json, double? ms = 5) =>
        new(++_seq, test, ObservationKinds.HttpResponse, trigger, JsonNode.Parse(json), ms, DateTimeOffset.UtcNow);

    public static Observation Write(string test, string trigger, string entity, string state, string valuesJson, string? foreignKeysJson = null) =>
        new(++_seq, test, ObservationKinds.DbWrite, trigger, new JsonObject
        {
            ["context"] = "Ctx",
            ["entity"] = entity,
            ["state"] = state,
            ["keys"] = new JsonArray("Id"),
            ["values"] = JsonNode.Parse(valuesJson),
            ["foreignKeys"] = foreignKeysJson is null ? null : JsonNode.Parse(foreignKeysJson),
        }, 2, DateTimeOffset.UtcNow);

    public static Recording Rec(string tree, params Observation[] observations) =>
        new(tree, tree, "abc", [], observations);
}
