using System.Text.Json.Nodes;

namespace BehaveDiff.Core;

/// <summary>
/// One thing the app did. Kind is open-ended ("http-response", "db-write", "test-result",
/// later "blob-written", "message-sent"); diffing is generic over the JSON in <see cref="Data"/>.
/// </summary>
public sealed record Observation(
    long Seq,
    string Test,
    string Kind,
    string Trigger,
    JsonNode? Data,
    double? DurationMs,
    DateTimeOffset Timestamp)
{
    /// <summary>The entity a db-write touched (data.entity); null for other kinds.</summary>
    public string? Entity => Kind == ObservationKinds.DbWrite ? (string?)Data?["entity"] : null;
}

public static class ObservationKinds
{
    public const string HttpResponse = "http-response";
    public const string DbWrite = "db-write";
    /// <summary>Synthesized from the test report: one per test, data = { outcome }.</summary>
    public const string TestResult = "test-result";
}

public static class SpecialTests
{
    /// <summary>Fixture code outside any test (AssemblyInitialize / ClassInitialize).</summary>
    public const string Setup = "(setup)";
    /// <summary>Observed outside every test's time window.</summary>
    public const string Unattributed = "(unattributed)";
}

public sealed record TestResult(string Name, string Outcome, DateTimeOffset Start, DateTimeOffset End);

/// <summary>Everything one test run of one tree produced.</summary>
public sealed record Recording(
    string Tree,
    string Ref,
    string Commit,
    IReadOnlyList<TestResult> Tests,
    IReadOnlyList<Observation> Observations);
