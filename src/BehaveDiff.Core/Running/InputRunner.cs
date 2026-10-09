using System.Text.RegularExpressions;

namespace BehaveDiff.Core.Running;

/// <summary>A source tree ready to build: the base worktree or the working tree.</summary>
public sealed record PreparedTree(string Name, string Root, string Ref, string Commit);

/// <summary>Drives the app and yields observations. v1: MSTest on Microsoft.Testing.Platform.</summary>
public interface IInputRunner
{
    string Kind { get; }
    string Project { get; }
    Task BuildAsync(PreparedTree tree, string artifactsDir, CancellationToken ct);
    Task<Recording> RunAsync(PreparedTree tree, string runName, string artifactsDir, CancellationToken ct);
}

sealed class MstestInputRunner(string project, string hookAssemblyPath, IReadOnlyList<string> extraBuildArgs, string? testNamePrefix) : IInputRunner
{
    public string Kind => "mstest";
    public string Project => project;

    string ProjectPath(PreparedTree tree) => Path.GetFullPath(Path.Combine(tree.Root, project));

    public async Task BuildAsync(PreparedTree tree, string artifactsDir, CancellationToken ct)
    {
        var proj = ProjectPath(tree);
        if (!File.Exists(proj))
            throw new BehaveDiffException(tree.Name, "build", $"Test project not found in the {TreeLabel(tree)}: {project}");

        // No capture env vars here: MSBuild processes must never load the hook.
        var args = new List<string> { "build", proj, "-nodeReuse:false", "-nologo", "-v", "q" };
        args.AddRange(extraBuildArgs.Where(a => !a.StartsWith("-nodeReuse", StringComparison.OrdinalIgnoreCase)));
        var log = Path.Combine(artifactsDir, $"build-{tree.Name}.log");
        var result = await ProcessRunner.RunAsync("dotnet", args, tree.Root, logFile: log, ct: ct);
        if (result.ExitCode != 0)
            throw new BehaveDiffException(tree.Name, "build",
                $"Build failed in the {TreeLabel(tree)}. Log: {log}", ErrorLines(result.Output));
    }

    public async Task<Recording> RunAsync(PreparedTree tree, string runName, string artifactsDir, CancellationToken ct)
    {
        var runDir = Path.Combine(artifactsDir, runName);
        var captureDir = Path.Combine(runDir, "capture");
        var trxDir = Path.Combine(runDir, "trx");
        Directory.CreateDirectory(captureDir);
        Directory.CreateDirectory(trxDir);

        var existingHooks = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        var env = new Dictionary<string, string?>
        {
            ["DOTNET_STARTUP_HOOKS"] = string.IsNullOrEmpty(existingHooks) ? hookAssemblyPath : existingHooks + Path.PathSeparator + hookAssemblyPath,
            ["BEHAVEDIFF_CAPTURE_DIR"] = captureDir,
        };
        var args = new List<string> { "test", "--project", ProjectPath(tree), "--no-build", "--report-trx", "--results-directory", trxDir };
        var log = Path.Combine(runDir, "test.log");
        var result = await ProcessRunner.RunAsync("dotnet", args, tree.Root, env, log, ct);

        // A non-zero exit with a TRX just means tests failed: that's a difference, not an error.
        var trx = Directory.Exists(trxDir) ? Directory.EnumerateFiles(trxDir, "*.trx", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (trx is null)
            throw new BehaveDiffException(tree.Name, "test",
                $"Test run produced no TRX report in the {TreeLabel(tree)} (exit {result.ExitCode}). Log: {log}", Tail(result.Output, 30));

        var hookErrors = Directory.EnumerateFiles(captureDir, "hook-*.log")
            .SelectMany(File.ReadLines)
            .Where(l => l.Contains("ERROR in "))
            .Distinct()
            .ToList();
        if (hookErrors.Count > 0)
            throw new BehaveDiffException(tree.Name, "capture", $"Capture hook reported errors in the {TreeLabel(tree)}.", string.Join('\n', hookErrors.Take(5)));

        return RecordingReader.Read(tree.Name, tree.Ref, tree.Commit, captureDir, trx, testNamePrefix);
    }

    static string TreeLabel(PreparedTree tree) => tree.Name == "current" ? "working tree" : $"base tree ({tree.Ref})";

    static readonly Regex ErrorLine = new(@": error [A-Z]+\d+:", RegexOptions.Compiled);

    internal static string ErrorLines(string output)
    {
        var errors = output.Split('\n').Select(l => l.Trim()).Where(l => ErrorLine.IsMatch(l)).Distinct().Take(20).ToList();
        return errors.Count > 0 ? string.Join('\n', errors) : Tail(output, 30);
    }

    static string Tail(string output, int lines) =>
        string.Join('\n', output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(lines));
}
