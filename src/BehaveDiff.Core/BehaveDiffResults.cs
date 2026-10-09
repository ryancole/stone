using System.Text.Json;
using BehaveDiff.Core.Classify;
using BehaveDiff.Core.Running;

namespace BehaveDiff.Core;

/// <summary>One difference with the full observations on each side (raw, before normalization).</summary>
public sealed record DifferenceDetail(Difference Difference, Observation? Base, Observation? Current);

public sealed record RunResult(string RunDirectory, Report Report);

/// <summary>Reads back finished runs: latest report, difference details, acceptances.</summary>
public static class BehaveDiffResults
{
    public const string ReportFileName = "report.json";
    public const string DetailsFileName = "details.json";

    /// <summary>Artifact roots for a repo, in the order <see cref="BehaveDiffRunner"/> may use them.</summary>
    public static IReadOnlyList<string> ArtifactRoots(string repoRoot) =>
    [
        Path.Combine(repoRoot, BehaveDiffRunner.ArtifactsDirName, "runs"),
        Path.Combine(Path.GetTempPath(), "behavediff", "runs", Path.GetFileName(repoRoot)),
    ];

    /// <summary>
    /// &lt;repo&gt;/.behavediff/accepted.json when git ignores .behavediff/ (behavediff init); otherwise next to
    /// the temp artifacts, so accepting never adds untracked files to the user's working tree.
    /// </summary>
    internal static async Task<string> AcceptedPathAsync(GitRepo repo, CancellationToken ct) =>
        await repo.IsIgnoredAsync($"{BehaveDiffRunner.ArtifactsDirName}/{BehaveDiffRunner.AcceptedFileName}", ct)
            ? Path.Combine(repo.Root, BehaveDiffRunner.ArtifactsDirName, BehaveDiffRunner.AcceptedFileName)
            : Path.Combine(Path.GetTempPath(), "behavediff", Path.GetFileName(repo.Root), BehaveDiffRunner.AcceptedFileName);

    /// <summary>The most recent run with a report, or null when the repo was never checked.</summary>
    public static async Task<RunResult?> LatestAsync(string repoDirectory, CancellationToken ct = default)
    {
        var repo = await GitRepo.OpenAsync(repoDirectory, ct);
        var latest = ArtifactRoots(repo.Root)
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(d => new FileInfo(Path.Combine(d, ReportFileName)))
            .Where(f => f.Exists)
            .MaxBy(f => f.LastWriteTimeUtc);
        if (latest is null) return null;
        var report = ReportJson.Deserialize(await File.ReadAllTextAsync(latest.FullName, ct))
            ?? throw new InvalidDataException($"Unreadable report: {latest.FullName}");
        return new RunResult(latest.DirectoryName!, report);
    }

    /// <summary>Both observations behind one difference of the latest run, or null if the id isn't in it.</summary>
    public static async Task<DifferenceDetail?> ExplainAsync(string repoDirectory, string id, CancellationToken ct = default)
    {
        var run = await LatestAsync(repoDirectory, ct);
        var difference = run?.Report.Differences.FirstOrDefault(d => d.Id == id);
        if (run is null || difference is null) return null;

        var detailsPath = Path.Combine(run.RunDirectory, DetailsFileName);
        if (!File.Exists(detailsPath)) return new DifferenceDetail(difference, null, null);
        var details = JsonSerializer.Deserialize<Dictionary<string, DifferenceDetail>>(await File.ReadAllTextAsync(detailsPath, ct), ReportJson.Options);
        return details?.GetValueOrDefault(id) ?? new DifferenceDetail(difference, null, null);
    }

    /// <summary>
    /// Records an acceptance in .behavediff/accepted.json. The id must be a difference in the latest run,
    /// so a typo can't silently accept nothing. Later runs report it under Accepted.
    /// </summary>
    public static async Task<Difference> AcceptAsync(string repoDirectory, string id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to accept a difference.", nameof(reason));
        var run = await LatestAsync(repoDirectory, ct)
            ?? throw new InvalidOperationException("No BehaveDiff run found for this repo; run a check first.");
        var difference = run.Report.Differences.FirstOrDefault(d => d.Id == id)
            ?? throw new KeyNotFoundException($"Difference '{id}' is not in the latest report ({run.RunDirectory}).");
        var repo = await GitRepo.OpenAsync(repoDirectory, ct);
        AcceptedStore.Add(await AcceptedPathAsync(repo, ct), id, reason.Trim());
        return difference;
    }

    internal static void WriteDetails(string runDirectory, IEnumerable<DifferenceDetail> details) =>
        File.WriteAllText(Path.Combine(runDirectory, DetailsFileName),
            JsonSerializer.Serialize(details.ToDictionary(d => d.Difference.Id), ReportJson.Options));
}
