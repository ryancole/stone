using System.Diagnostics;
using BehaveDiff.Core;

namespace BehaveDiff.Cli;

/// <summary>behavediff init: writes .behavediff.yml and gitignores BehaveDiff's local files.</summary>
static class InitCommand
{
    static readonly string[] IgnoreEntries = [$"{BehaveDiffRunner.ArtifactsDirName}/", ConfigLoader.LocalFileName];

    public static int Run(string directory, TextWriter w)
    {
        var root = RepoRoot(directory);
        if (root is null)
        {
            w.WriteLine($"Not inside a git repository: {directory}");
            return 2;
        }

        var configPath = Path.Combine(root, ConfigLoader.FileName);
        if (File.Exists(configPath))
            w.WriteLine($"{ConfigLoader.FileName} already exists; left unchanged.");
        else
        {
            var candidates = TestProjects(root);
            File.WriteAllText(configPath, Template(candidates.Count == 1 ? candidates[0] : null));
            w.WriteLine($"Created {ConfigLoader.FileName}.");
            if (candidates.Count != 1)
            {
                w.WriteLine(candidates.Count == 0
                    ? "  No MSTest project found; set inputs[0].project."
                    : "  Several MSTest projects found; pick one for inputs[0].project:");
                foreach (var c in candidates) w.WriteLine($"    {c}");
            }
        }

        var gitignore = Path.Combine(root, ".gitignore");
        var existing = File.Exists(gitignore) ? File.ReadAllLines(gitignore) : [];
        var missing = IgnoreEntries.Where(e => !existing.Any(l => l.Trim() == e)).ToList();
        if (missing.Count > 0)
        {
            var prefix = existing.Length > 0 && existing[^1].Trim().Length > 0 ? Environment.NewLine : "";
            File.AppendAllText(gitignore, prefix + "# BehaveDiff local files (run artifacts can contain real data)" + Environment.NewLine
                + string.Join(Environment.NewLine, missing) + Environment.NewLine);
            w.WriteLine($"Added to .gitignore: {string.Join(", ", missing)}");
        }
        return 0;
    }

    static string Template(string? project) => $"""
        # BehaveDiff config. Shared and committable: no secrets. Per-machine overrides: {ConfigLoader.LocalFileName}
        # baseRef: master          # default: the remote's default branch
        inputs:
          - kind: mstest
            project: {project ?? "src/Tests/Your.Tests/Your.Tests.csproj"}
        ignore:
          jsonPaths: []            # e.g. "$.body.items[*].etag"
          headers: ["Date", "traceparent", "tracestate", "Request-Context", "Server"]
          dbColumns: []            # "Entity.Column" or "Column"
        latencyRegressionPct: 100
        selfNoiseCheck: true

        """;

    /// <summary>csproj files referencing MSTest, relative to the repo root.</summary>
    static List<string> TestProjects(string root) =>
        Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(p => File.ReadAllText(p).Contains("MSTest", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(p => p)
            .ToList();

    static string? RepoRoot(string directory)
    {
        var psi = new ProcessStartInfo("git", "rev-parse --show-toplevel") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return p.ExitCode == 0 ? Path.GetFullPath(output) : null;
    }
}
