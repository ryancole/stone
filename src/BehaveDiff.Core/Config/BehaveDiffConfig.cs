using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BehaveDiff.Core;

/// <summary>.behavediff.yml. Every per-app fact lives here; defaults are generic.</summary>
public sealed class BehaveDiffConfig
{
    /// <summary>Null: the remote's default branch (origin/HEAD), else main, else master.</summary>
    public string? BaseRef { get; set; }
    public BuildConfig Build { get; set; } = new();
    public List<InputConfig> Inputs { get; set; } = [];
    public IgnoreConfig Ignore { get; set; } = new();
    public IdentityConfig Identity { get; set; } = new();
    public double LatencyRegressionPct { get; set; } = 100;
    public bool SelfNoiseCheck { get; set; } = true;
    /// <summary>Untracked files copied from the working tree into the base worktree (globs, relative to repo root).</summary>
    public List<string> CopyUntracked { get; set; } = ["**/appsettings*.json"];

    public sealed class BuildConfig
    {
        public List<string> ExtraArgs { get; set; } = [];
    }

    public sealed class InputConfig
    {
        public string Kind { get; set; } = "mstest";
        public string Project { get; set; } = "";
    }

    public sealed class IgnoreConfig
    {
        /// <summary>Paths inside observation data, e.g. "$.body.items[*].etag".</summary>
        public List<string> JsonPaths { get; set; } = [];
        public List<string> Headers { get; set; } = ["Date", "traceparent", "tracestate", "Request-Context", "Server"];
        /// <summary>"Entity.Column" or "Column".</summary>
        public List<string> DbColumns { get; set; } = [];
    }

    public sealed class IdentityConfig
    {
        public bool Enabled { get; set; } = true;
        /// <summary>JSON field names treated as row ids when comparing (regex).</summary>
        public string FieldPattern { get; set; } = "^id$|Id$";
    }
}

public static class ConfigLoader
{
    public const string FileName = ".behavediff.yml";
    public const string LocalFileName = ".behavediff.local.yml";

    /// <summary>
    /// <paramref name="explicitPath"/> (--config) if given, else &lt;repo&gt;/.behavediff.yml, else defaults;
    /// then &lt;repo&gt;/.behavediff.local.yml overlaid on top.
    /// </summary>
    public static BehaveDiffConfig Load(string repoRoot, string? explicitPath)
    {
        var path = explicitPath ?? Path.Combine(repoRoot, FileName);
        if (explicitPath is not null && !File.Exists(explicitPath))
            throw new BehaveDiffException("tool", "config", $"Config file not found: {explicitPath}");

        var config = File.Exists(path) ? Parse(File.ReadAllText(path), path) : new BehaveDiffConfig();
        var local = Path.Combine(repoRoot, LocalFileName);
        if (File.Exists(local))
            Overlay(config, File.ReadAllText(local), local);
        return config;
    }

    static IDeserializer Deserializer() => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    internal static BehaveDiffConfig Parse(string yaml, string source)
    {
        try { return Deserializer().Deserialize<BehaveDiffConfig?>(yaml) ?? new BehaveDiffConfig(); }
        catch (Exception ex) { throw new BehaveDiffException("tool", "config", $"Invalid config {source}: {ex.Message}"); }
    }

    /// <summary>Local overrides: scalars replace, lists replace, only for keys present in the file.</summary>
    static void Overlay(BehaveDiffConfig target, string yaml, string source)
    {
        Dictionary<string, object?>? raw;
        try { raw = Deserializer().Deserialize<Dictionary<string, object?>?>(yaml); }
        catch (Exception ex) { throw new BehaveDiffException("tool", "config", $"Invalid config {source}: {ex.Message}"); }
        if (raw is null) return;

        var overlay = Parse(yaml, source);
        if (raw.ContainsKey("baseRef")) target.BaseRef = overlay.BaseRef;
        if (raw.ContainsKey("build")) target.Build = overlay.Build;
        if (raw.ContainsKey("inputs")) target.Inputs = overlay.Inputs;
        if (raw.ContainsKey("ignore")) target.Ignore = overlay.Ignore;
        if (raw.ContainsKey("identity")) target.Identity = overlay.Identity;
        if (raw.ContainsKey("latencyRegressionPct")) target.LatencyRegressionPct = overlay.LatencyRegressionPct;
        if (raw.ContainsKey("selfNoiseCheck")) target.SelfNoiseCheck = overlay.SelfNoiseCheck;
        if (raw.ContainsKey("copyUntracked")) target.CopyUntracked = overlay.CopyUntracked;
    }
}

/// <summary>A run-stopping problem, reported as a <see cref="RunError"/> (exit 2).</summary>
public sealed class BehaveDiffException(string tree, string stage, string message, string? detail = null) : Exception(message)
{
    public RunError ToRunError() => new(tree, stage, Message, detail);
}
