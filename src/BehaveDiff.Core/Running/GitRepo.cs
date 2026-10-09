using System.Text.RegularExpressions;

namespace BehaveDiff.Core.Running;

/// <summary>Read-mostly git operations on the user's repo. The only writes are worktree add/remove.</summary>
sealed class GitRepo(string root)
{
    public string Root { get; } = root;

    public static async Task<GitRepo> OpenAsync(string directory, CancellationToken ct)
    {
        var top = await Git(directory, ct, "rev-parse", "--show-toplevel");
        if (top.ExitCode != 0)
            throw new BehaveDiffException("tool", "git", $"Not inside a git repository: {directory}", top.Output.Trim());
        return new GitRepo(Path.GetFullPath(top.Output.Trim()));
    }

    /// <summary>
    /// Configured ref if given; else the remote's default branch (preferring the local branch of
    /// that name), else main, else master.
    /// </summary>
    public async Task<string> ResolveBaseRefAsync(string? configured, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!await RefExistsAsync(configured, ct))
                throw new BehaveDiffException("base", "git", $"Base ref '{configured}' does not exist.");
            return configured;
        }

        var head = await Git(Root, ct, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD");
        if (head.ExitCode == 0)
        {
            var remoteBranch = head.Output.Trim();                 // origin/master
            var local = remoteBranch[(remoteBranch.IndexOf('/') + 1)..];
            return await RefExistsAsync(local, ct) ? local : remoteBranch;
        }
        foreach (var candidate in new[] { "main", "master" })
            if (await RefExistsAsync(candidate, ct))
                return candidate;
        throw new BehaveDiffException("base", "git", "Could not determine the base branch. Set baseRef in .behavediff.yml or pass --base.");
    }

    async Task<bool> RefExistsAsync(string reference, CancellationToken ct) =>
        (await Git(Root, ct, "rev-parse", "--verify", "--quiet", reference + "^{commit}")).ExitCode == 0;

    public async Task<string> ShortCommitAsync(string reference, CancellationToken ct) =>
        (await Git(Root, ct, "rev-parse", "--short", reference)).Output.Trim();

    public async Task<bool> IsDirtyAsync(CancellationToken ct) =>
        (await Git(Root, ct, "status", "--porcelain")).Output.Trim().Length > 0;

    /// <summary>True when git would ignore <paramref name="relativePath"/>.</summary>
    public async Task<bool> IsIgnoredAsync(string relativePath, CancellationToken ct) =>
        (await Git(Root, ct, "check-ignore", "--quiet", "--no-index", relativePath)).ExitCode == 0;

    public async Task AddDetachedWorktreeAsync(string path, string reference, CancellationToken ct)
    {
        var result = await Git(Root, ct, "worktree", "add", "--detach", "--force", path, reference);
        if (result.ExitCode != 0)
            throw new BehaveDiffException("base", "git", $"git worktree add failed for '{reference}'.", result.Output.Trim());
    }

    /// <summary>Best effort: removes the worktree and prunes its registration.</summary>
    public async Task RemoveWorktreeAsync(string path)
    {
        try { await Git(Root, CancellationToken.None, "worktree", "remove", "--force", path); } catch { }
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        try { await Git(Root, CancellationToken.None, "worktree", "prune"); } catch { }
    }

    /// <summary>Untracked or ignored files matching any glob, relative to the repo root.</summary>
    public async Task<IReadOnlyList<string>> UntrackedFilesAsync(IEnumerable<string> globs, CancellationToken ct)
    {
        var patterns = globs.Select(GlobToRegex).ToList();
        if (patterns.Count == 0) return [];
        var others = await Git(Root, ct, "ls-files", "--others", "--exclude-standard");
        var ignored = await Git(Root, ct, "ls-files", "--others", "--ignored", "--exclude-standard");
        return (others.Output + "\n" + ignored.Output)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/") && !f.StartsWith("bin/") && !f.StartsWith("obj/"))
            .Where(f => patterns.Any(p => p.IsMatch(f)))
            .Distinct()
            .ToList();
    }

    internal static Regex GlobToRegex(string glob)
    {
        var pattern = Regex.Escape(glob.Replace('\\', '/'))
            .Replace(@"\*\*/", "(.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");
        return new Regex("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    static Task<ProcessResult> Git(string cwd, CancellationToken ct, params string[] args) =>
        ProcessRunner.RunAsync("git", args, cwd, ct: ct);
}
