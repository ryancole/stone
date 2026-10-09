using System.ComponentModel;
using System.Text.Json;
using BehaveDiff.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BehaveDiff.Mcp;

/// <summary>Where the server works: the repo it was started in, plus optional --config.</summary>
public sealed record BehaveDiffServerOptions(string RepoDirectory, string HookAssemblyPath, string? ConfigPath);

[McpServerToolType]
public sealed class BehaveDiffTools(BehaveDiffServerOptions options)
{
    // Stages in the order a run reports them; drives the progress fraction.
    static readonly string[] Stages = ["setup", "build", "test", "diff", "cleanup"];

    [McpServerTool(Name = "check_behavior_changes", Title = "Check behavior changes", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Compares the app's runtime behavior in the current working tree against the base branch. Builds both, runs
        the configured test suite against each with capture injected, and diffs what the app did: HTTP responses,
        database writes and test outcomes. Call it before declaring a code change done. Takes minutes.

        Returns the report JSON. exitCode 0 = no differences, 1 = differences, 2 = the comparison could not run (see
        errors[]: a build failure names the tree and compiler error). Every difference has a stable id and a
        classification: Unexpected (fix it, or accept it with a reason if it is correct), Intended (matched an
        expect rule), Accepted (accepted earlier). Use explain_difference for the full observations behind an id.
        """)]
    public async Task<string> CheckBehaviorChanges(
        [Description("What the change is supposed to do, in a sentence. Stored in the report.")] string intent,
        [Description("Differences the change is meant to cause: a trigger such as \"POST /widgets\" or \"GET /widgets/*\", or \"entity:<EntityName>\" for database writes of that entity. Matching differences are classified Intended.")] string[]? expect = null,
        [Description("Base ref to compare against. Default: baseRef from .behavediff.yml, else the remote's default branch.")] string? baseRef = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var events = new Progress<RunEvent>(e =>
        {
            Console.Error.WriteLine($"[behavediff] [{e.Stage}] {e.Message}");
            var stage = Array.IndexOf(Stages, e.Stage);
            progress?.Report(new ProgressNotificationValue { Progress = Math.Max(stage, 0), Total = Stages.Length, Message = e.Message });
        });

        var report = await BehaveDiffRunner.RunAsync(new RunOptions
        {
            RepoDirectory = options.RepoDirectory,
            HookAssemblyPath = options.HookAssemblyPath,
            ConfigPath = options.ConfigPath,
            BaseRef = baseRef,
            Intent = intent,
            Expect = expect ?? [],
        }, events, cancellationToken);
        return ReportJson.Serialize(report);
    }

    [McpServerTool(Name = "explain_difference", Title = "Explain a difference", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Shows one difference from the latest check_behavior_changes run in full: the difference itself plus the
        complete base and current observations it came from (the whole HTTP response or database write, before
        normalization), and the test that produced them.
        """)]
    public async Task<string> ExplainDifference(
        [Description("Difference id from the report, e.g. \"d-3f9a1c2e\".")] string id,
        CancellationToken cancellationToken = default)
    {
        var detail = await BehaveDiffResults.ExplainAsync(options.RepoDirectory, id.Trim(), cancellationToken)
            ?? throw new McpException($"Difference '{id}' is not in the latest report. Run check_behavior_changes first, and use an id from its differences[].");
        return JsonSerializer.Serialize(detail, ReportJson.Options);
    }

    [McpServerTool(Name = "accept_difference", Title = "Accept a difference", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Accepts a difference from the latest run as correct, with a reason. It is recorded in
        .behavediff/accepted.json and reported under Accepted (not Unexpected) by later runs. Only accept a difference
        you have checked is a correct consequence of the change; otherwise fix the code.
        """)]
    public async Task<string> AcceptDifference(
        [Description("Difference id from the latest report, e.g. \"d-3f9a1c2e\".")] string id,
        [Description("Why this difference is correct, in a sentence a reviewer can check.")] string reason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var difference = await BehaveDiffResults.AcceptAsync(options.RepoDirectory, id.Trim(), reason, cancellationToken);
            return JsonSerializer.Serialize(new
            {
                accepted = difference.Id,
                reason = reason.Trim(),
                difference.Kind,
                difference.Test,
                difference.ObservationKind,
                difference.Trigger,
                difference.Path,
            }, ReportJson.Options);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new McpException(ex.Message);
        }
    }
}
