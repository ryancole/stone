using System.Text.Json.Nodes;

namespace BehaveDiff.Core;

public enum DifferenceKind
{
    ValueChanged, TypeChanged, FieldAdded, FieldRemoved,
    ObservationAdded, ObservationRemoved,
}

public enum Classification { Unexpected, Intended, Accepted }

/// <summary>What was paired: same test, kind, trigger, entity, and nth occurrence of that group.</summary>
public sealed record MatchKey(string Test, string Kind, string Trigger, string? Entity, int Occurrence);

/// <summary>Why a difference landed where it did. Source: "default" | "expect" | "accepted" (later "llm").</summary>
public sealed record ClassificationReason(string Source, string? Rule, string? Note)
{
    public static readonly ClassificationReason Default = new("default", null, null);
}

public sealed record Difference(
    string Id,
    DifferenceKind Kind,
    Classification Classification,
    ClassificationReason Reason,
    string Test,
    string ObservationKind,
    string Trigger,
    string? Entity,
    string? Path,
    JsonNode? Before,
    JsonNode? After,
    MatchKey Match);

public sealed record LatencyFlag(MatchKey Match, double BaseMs, double CurrentMs, double ChangePct);

/// <summary>A path ignored for a kind + trigger. Origin: "config" | "self-noise" | "default".</summary>
public sealed record NoiseRule(string Kind, string Trigger, string Path, string Origin);

public sealed record MovedObservation(MatchKey Base, MatchKey Current);

public sealed record TreeInfo(string Ref, string Commit, int Tests, int Passed);

public sealed record InputInfo(string Kind, string Project);

public sealed record RunInfo(
    TreeInfo? Base,
    TreeInfo? Current,
    IReadOnlyList<InputInfo> Inputs,
    DateTimeOffset StartedAt,
    double DurationMs,
    string? ArtifactsDir);

public sealed record IntentInfo(string? Text, IReadOnlyList<string> Expect);

public sealed record ReportSummary(
    int Unexpected,
    int Intended,
    int Accepted,
    int ObservationsCompared,
    int Unchanged,
    int NoiseRules,
    int LatencyFlags,
    int Moved);

/// <summary>Tree: "base" | "current" | "tool". Stage: "config" | "git" | "build" | "test" | "capture" | "attribution".</summary>
public sealed record RunError(string Tree, string Stage, string Message, string? Detail);

public sealed record Report(
    int SchemaVersion,
    RunInfo Run,
    IntentInfo Intent,
    ReportSummary Summary,
    IReadOnlyList<Difference> Differences,
    IReadOnlyList<LatencyFlag> Latency,
    IReadOnlyList<NoiseRule> Noise,
    IReadOnlyList<MovedObservation> Moved,
    IReadOnlyList<RunError> Errors)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>2 on any error; 1 when any unexpected or intended difference exists; else 0.</summary>
    public int ExitCode =>
        Errors.Count > 0 ? 2
        : Differences.Any(d => d.Classification is Classification.Unexpected or Classification.Intended) ? 1
        : 0;
}
