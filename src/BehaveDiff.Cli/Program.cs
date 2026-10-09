using System.CommandLine;
using BehaveDiff.Cli;
using BehaveDiff.Core;

var repoOption = new Option<string>("--repo") { Description = "Directory inside the repo to check (default: current directory)" };
var configOption = new Option<string?>("--config") { Description = "Config file to use instead of <repo>/.behavediff.yml (may live outside the repo)" };
var baseOption = new Option<string?>("--base") { Description = "Base ref to compare against (default: baseRef from config, else the remote's default branch)" };
var projectOption = new Option<string?>("--project") { Description = "Test project (relative to repo root); overrides config inputs" };
var intentOption = new Option<string?>("--intent") { Description = "What the change is supposed to do (stored in the report)" };
var expectOption = new Option<string[]>("--expect")
{
    Description = "Mark matching differences as intended: a trigger (\"POST /widgets\", \"GET /widgets/*\") or \"entity:<Name>\". Repeatable.",
    AllowMultipleArgumentsPerToken = false,
};
var noSelfNoiseOption = new Option<bool>("--no-self-noise") { Description = "Skip the second base run that detects noisy fields" };
var jsonOption = new Option<bool>("--json") { Description = "Write the report as JSON to stdout" };
var outOption = new Option<string?>("--out") { Description = "Also write the JSON report to this file" };
var keepOption = new Option<bool>("--keep") { Description = "Keep the base worktree for inspection" };

var run = new Command("run", "Compare runtime behavior of the working tree against the base ref")
{
    repoOption, configOption, baseOption, projectOption, intentOption, expectOption, noSelfNoiseOption, jsonOption, outOption, keepOption,
};
run.SetAction(async (parse, ct) =>
{
    var json = parse.GetValue(jsonOption);
    var progress = new Progress<RunEvent>(e => Console.Error.WriteLine($"[{e.Stage}] {e.Message}"));
    var report = await BehaveDiffRunner.RunAsync(new RunOptions
    {
        RepoDirectory = Path.GetFullPath(parse.GetValue(repoOption) ?? Environment.CurrentDirectory),
        HookAssemblyPath = Path.Combine(AppContext.BaseDirectory, "BehaveDiff.Capture.dll"),
        ConfigPath = parse.GetValue(configOption) is { } c ? Path.GetFullPath(c) : null,
        BaseRef = parse.GetValue(baseOption),
        Project = parse.GetValue(projectOption),
        Intent = parse.GetValue(intentOption),
        Expect = parse.GetValue(expectOption) ?? [],
        SelfNoiseCheck = parse.GetValue(noSelfNoiseOption) ? false : null,
        KeepArtifacts = parse.GetValue(keepOption),
    }, progress, ct);

    if (parse.GetValue(outOption) is { } outFile)
        await File.WriteAllTextAsync(outFile, ReportJson.Serialize(report), ct);
    if (json)
        Console.Out.WriteLine(ReportJson.Serialize(report));
    else
        TextReport.Write(report, Console.Out);
    return report.ExitCode;
});

var init = new Command("init", "Create .behavediff.yml and gitignore BehaveDiff's local files in the current repo")
{
    repoOption,
};
init.SetAction(parse => InitCommand.Run(Path.GetFullPath(parse.GetValue(repoOption) ?? Environment.CurrentDirectory), Console.Out));

var root = new RootCommand("BehaveDiff: what did my uncommitted changes do to the app's runtime behavior?") { run, init };
return await root.Parse(args).InvokeAsync();
