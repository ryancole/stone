using System.Diagnostics;
using BehaveDiff.Core.Classify;
using BehaveDiff.Core.Diffing;
using BehaveDiff.Core.Running;

namespace BehaveDiff.Core;

public sealed record RunOptions
{
    /// <summary>Any directory inside the repo to check.</summary>
    public required string RepoDirectory { get; init; }
    /// <summary>BehaveDiff.Capture.dll, injected into test hosts.</summary>
    public required string HookAssemblyPath { get; init; }
    public string? ConfigPath { get; init; }
    public string? BaseRef { get; init; }
    /// <summary>Overrides config inputs with a single MSTest project.</summary>
    public string? Project { get; init; }
    public string? Intent { get; init; }
    public IReadOnlyList<string> Expect { get; init; } = [];
    public bool? SelfNoiseCheck { get; init; }
    /// <summary>Keep the base worktree and all run artifacts.</summary>
    public bool KeepArtifacts { get; init; }
}

public sealed record RunEvent(string Stage, string Message);

/// <summary>
/// behavediff run: base worktree + working tree, build both, run tests with capture, (optionally run
/// base twice for self-noise), normalize, match, classify, report. Always cleans up the worktree.
/// </summary>
public static class BehaveDiffRunner
{
    public const string ArtifactsDirName = ".behavediff";
    public const string AcceptedFileName = "accepted.json";

    public static async Task<Report> RunAsync(RunOptions options, IProgress<RunEvent>? progress = null, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        void Emit(string stage, string message) => progress?.Report(new RunEvent(stage, message));

        GitRepo? repo = null;
        BehaveDiffConfig config = new();
        string? artifacts = null;
        string? worktree = null;
        TreeInfo? baseInfo = null, currentInfo = null;
        var inputs = new List<IInputRunner>();

        Report Fail(RunError error) => new(
            Core.Report.CurrentSchemaVersion,
            new RunInfo(baseInfo, currentInfo, inputs.Select(i => new InputInfo(i.Kind, i.Project)).ToList(), started, clock.Elapsed.TotalMilliseconds, artifacts),
            new IntentInfo(options.Intent, options.Expect),
            new ReportSummary(0, 0, 0, 0, 0, 0, 0, 0),
            [], [], [], [], [error]);

        try
        {
            repo = await GitRepo.OpenAsync(options.RepoDirectory, ct);
            config = ConfigLoader.Load(repo.Root, options.ConfigPath);
            if (options.Project is not null)
                config.Inputs = [new BehaveDiffConfig.InputConfig { Kind = "mstest", Project = options.Project }];
            if (config.Inputs.Count == 0)
                throw new BehaveDiffException("tool", "config", "No test project configured. Add inputs to .behavediff.yml (behavediff init) or pass --project.");
            foreach (var input in config.Inputs.Where(i => i.Kind != "mstest"))
                throw new BehaveDiffException("tool", "config", $"Unsupported input kind '{input.Kind}' (v1 supports mstest).");

            var prefixNames = config.Inputs.Count > 1;
            inputs.AddRange(config.Inputs.Select(i => (IInputRunner)new MstestInputRunner(
                i.Project.Replace('\\', '/'), options.HookAssemblyPath, config.Build.ExtraArgs,
                prefixNames ? Path.GetFileNameWithoutExtension(i.Project) : null)));

            artifacts = await ArtifactsDirAsync(repo, ct);
            Emit("setup", $"Artifacts: {artifacts}");

            var baseRef = await repo.ResolveBaseRefAsync(options.BaseRef ?? config.BaseRef, ct);
            var baseCommit = await repo.ShortCommitAsync(baseRef, ct);
            var headCommit = await repo.ShortCommitAsync("HEAD", ct);
            var dirty = await repo.IsDirtyAsync(ct);

            worktree = Path.Combine(Path.GetTempPath(), "behavediff", $"base-{Guid.NewGuid():N}"[..20]);
            Emit("setup", $"Base: {baseRef} ({baseCommit}) -> worktree {worktree}");
            await repo.AddDetachedWorktreeAsync(worktree, baseRef, ct);
            foreach (var file in await repo.UntrackedFilesAsync(config.CopyUntracked, ct))
            {
                var dest = Path.Combine(worktree, file);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(Path.Combine(repo.Root, file), dest, overwrite: true);
                Emit("setup", $"Copied untracked {file} into base tree");
            }

            // Known before anything runs, so an early failure still reports what was compared.
            baseInfo = new TreeInfo(baseRef, baseCommit, 0, 0);
            currentInfo = new TreeInfo("working tree", dirty ? headCommit + "-dirty" : headCommit, 0, 0);

            var baseTree = new PreparedTree("base", worktree, baseRef, baseCommit);
            var currentTree = new PreparedTree("current", repo.Root, "working tree", dirty ? headCommit + "-dirty" : headCommit);

            foreach (var tree in new[] { baseTree, currentTree })
                foreach (var input in inputs)
                {
                    Emit("build", $"Building {input.Project} ({tree.Name})");
                    await input.BuildAsync(tree, artifacts, ct);
                }

            var selfNoise = options.SelfNoiseCheck ?? config.SelfNoiseCheck;
            Emit("test", "Running base tests");
            var baseRec = await RunTree(inputs, baseTree, "base", artifacts, ct);
            Recording? baseRec2 = null;
            if (selfNoise)
            {
                Emit("test", "Running base tests again (self-noise check)");
                baseRec2 = await RunTree(inputs, baseTree, "base-2", artifacts, ct);
            }
            Emit("test", "Running working-tree tests");
            var currentRec = await RunTree(inputs, currentTree, "current", artifacts, ct);
            baseInfo = Info(baseRec);
            currentInfo = Info(currentRec);

            Emit("diff", "Comparing");
            var normalizer = new Normalizer(config.Ignore);
            var matcher = new Matcher(config.Identity, config.LatencyRegressionPct);
            var baseN = normalizer.Normalize(baseRec);
            var currentN = normalizer.Normalize(currentRec);

            var noise = config.Ignore.JsonPaths.Select(p => new NoiseRule("*", "*", p, "config")).ToList();
            if (baseRec2 is not null)
                noise.AddRange(SelfNoiseRules(matcher.Match(baseN, normalizer.Normalize(baseRec2))));

            var match = matcher.Match(baseN, currentN);
            var accepted = AcceptedStore.Load(Path.Combine(repo.Root, ArtifactsDirName, AcceptedFileName)).ToDictionary(a => a.Id);
            var kept = match.Diffs.Where(d => !noise.Any(rule => Suppresses(rule, d))).ToList();
            var differences = kept
                .Select(d => Classifier.Classify(d, options.Expect, accepted))
                .OrderBy(d => d.Classification)
                .ThenBy(d => d.Trigger, StringComparer.Ordinal)
                .ThenBy(d => d.Test, StringComparer.Ordinal)
                .ThenBy(d => d.Match.Occurrence)
                .ThenBy(d => d.Path, StringComparer.Ordinal)
                .ToList();

            var changedPairs = kept.Where(d => d.Base is not null && d.Current is not null).Select(d => d.Key).ToHashSet();
            var report = new Report(
                Core.Report.CurrentSchemaVersion,
                new RunInfo(baseInfo, currentInfo, inputs.Select(i => new InputInfo(i.Kind, i.Project)).ToList(), started, clock.Elapsed.TotalMilliseconds, artifacts),
                new IntentInfo(options.Intent, options.Expect),
                new ReportSummary(
                    differences.Count(d => d.Classification == Classification.Unexpected),
                    differences.Count(d => d.Classification == Classification.Intended),
                    differences.Count(d => d.Classification == Classification.Accepted),
                    match.PairedKeys.Count,
                    match.PairedKeys.Count(k => !changedPairs.Contains(k)),
                    noise.Count,
                    match.Latency.Count,
                    match.Moved.Count),
                differences,
                match.Latency,
                noise,
                match.Moved,
                []);
            await WriteReportAsync(report, artifacts);
            return report;
        }
        catch (BehaveDiffException ex)
        {
            var report = Fail(ex.ToRunError());
            if (artifacts is not null) await WriteReportAsync(report, artifacts);
            return report;
        }
        finally
        {
            if (repo is not null && worktree is not null && !options.KeepArtifacts)
            {
                Emit("cleanup", "Removing base worktree");
                await repo.RemoveWorktreeAsync(worktree);
            }
        }
    }

    static async Task<Recording> RunTree(IReadOnlyList<IInputRunner> inputs, PreparedTree tree, string runName, string artifacts, CancellationToken ct)
    {
        var recordings = new List<Recording>();
        foreach (var input in inputs)
            recordings.Add(await input.RunAsync(tree, inputs.Count > 1 ? $"{runName}-{Path.GetFileNameWithoutExtension(input.Project)}" : runName, artifacts, ct));
        if (recordings.Count == 1) return recordings[0];

        // Several inputs: one recording, seqs offset so they stay unique and ordered.
        var all = new List<Observation>();
        long offset = 0;
        foreach (var r in recordings)
        {
            all.AddRange(r.Observations.Select(o => o with { Seq = o.Seq + offset }));
            offset = all.Count == 0 ? offset : all.Max(o => o.Seq);
        }
        return new Recording(tree.Name, tree.Ref, tree.Commit, recordings.SelectMany(r => r.Tests).ToList(), all);
    }

    static TreeInfo Info(Recording r) =>
        new(r.Ref, r.Commit, r.Tests.Count, r.Tests.Count(t => t.Outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Anything that differs between two runs of the same base is noise by definition.</summary>
    internal static IEnumerable<NoiseRule> SelfNoiseRules(MatchResult baseVsBase) =>
        baseVsBase.Diffs
            .Select(d => new NoiseRule(d.Key.Kind, d.Key.Trigger, d.Path is null ? "$" : PathPattern.Generalize(d.Path), "self-noise"))
            .Distinct();

    internal static bool Suppresses(NoiseRule rule, DiffItem d) =>
        (rule.Kind == "*" || rule.Kind == d.Key.Kind)
        && (rule.Trigger == "*" || rule.Trigger == d.Key.Trigger)
        && PathPattern.Matches(rule.Path, d.Path);

    /// <summary>
    /// &lt;repo&gt;/.behavediff/runs/&lt;stamp&gt; when git ignores .behavediff/ (run data can contain real
    /// response bodies); otherwise a temp directory, so the working tree stays clean.
    /// </summary>
    static async Task<string> ArtifactsDirAsync(GitRepo repo, CancellationToken ct)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var root = await repo.IsIgnoredAsync($"{ArtifactsDirName}/runs/x", ct)
            ? Path.Combine(repo.Root, ArtifactsDirName, "runs")
            : Path.Combine(Path.GetTempPath(), "behavediff", "runs", Path.GetFileName(repo.Root));
        var dir = Path.Combine(root, stamp);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static Task WriteReportAsync(Report report, string artifacts) =>
        File.WriteAllTextAsync(Path.Combine(artifacts, "report.json"), ReportJson.Serialize(report));
}
