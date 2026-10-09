using System.Text.Json;
using System.Text.Json.Nodes;
using BehaveDiff.Core;

namespace BehaveDiff.Cli;

/// <summary>Human-readable report: unexpected first, grouped by trigger, then intended, accepted, and the rest.</summary>
static class TextReport
{
    const int MaxValueLength = 160;

    public static void Write(Report report, TextWriter w)
    {
        var run = report.Run;
        w.WriteLine();
        w.WriteLine($"BehaveDiff: {Describe(run.Base)} -> {Describe(run.Current)}");
        if (report.Intent.Text is { } intent) w.WriteLine($"Intent: {intent}");
        if (report.Intent.Expect.Count > 0) w.WriteLine($"Expect: {string.Join(", ", report.Intent.Expect)}");

        if (report.Errors.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("ERROR");
            foreach (var e in report.Errors)
            {
                w.WriteLine($"  [{e.Tree}/{e.Stage}] {e.Message}");
                if (e.Detail is { } detail)
                    foreach (var line in detail.Split('\n').Take(25))
                        w.WriteLine($"    {line}");
            }
            Footer(report, w);
            return;
        }

        Section(w, "UNEXPECTED", report.Differences.Where(d => d.Classification == Classification.Unexpected).ToList());
        Section(w, "INTENDED", report.Differences.Where(d => d.Classification == Classification.Intended).ToList());
        Section(w, "ACCEPTED", report.Differences.Where(d => d.Classification == Classification.Accepted).ToList());

        if (report.Moved.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"MOVED between tests ({report.Moved.Count}, not a difference)");
            foreach (var m in report.Moved)
                w.WriteLine($"  {m.Base.Kind} {m.Base.Trigger}{(m.Base.Entity is { } e ? $" {e}" : "")}: {m.Base.Test} -> {m.Current.Test}");
        }

        if (report.Latency.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"LATENCY (informational, {report.Latency.Count})");
            foreach (var l in report.Latency)
                w.WriteLine($"  {l.Match.Trigger}  {l.BaseMs} ms -> {l.CurrentMs} ms (+{l.ChangePct}%)  [{l.Match.Test}]");
        }

        var selfNoise = report.Noise.Where(n => n.Origin == "self-noise").ToList();
        if (selfNoise.Count > 0)
        {
            w.WriteLine();
            w.WriteLine($"NOISE ignored (differed between two base runs, {selfNoise.Count})");
            foreach (var n in selfNoise)
                w.WriteLine($"  {n.Kind} {n.Trigger}  {n.Path}");
        }

        Footer(report, w);
    }

    static void Section(TextWriter w, string title, IReadOnlyList<Difference> diffs)
    {
        if (diffs.Count == 0) return;
        w.WriteLine();
        w.WriteLine($"{title} ({diffs.Count})");
        foreach (var byTrigger in diffs.GroupBy(d => (d.ObservationKind, d.Trigger)))
        {
            w.WriteLine($"  {byTrigger.Key.Trigger}  ({byTrigger.Key.ObservationKind})");
            foreach (var d in byTrigger)
            {
                var where = d.Path ?? (d.Kind == DifferenceKind.ObservationAdded ? "new observation" : "observation no longer produced");
                var entity = d.Entity is { } e ? $"{e} " : "";
                w.WriteLine($"    [{d.Id}] {d.Kind} {entity}{where}");
                if (d.Kind is DifferenceKind.ObservationAdded or DifferenceKind.ObservationRemoved)
                    w.WriteLine($"        {Format(d.Before ?? d.After)}");
                else
                    w.WriteLine($"        {Format(d.Before)}  ->  {Format(d.After)}");
                w.WriteLine($"        test: {d.Test}{(d.Reason.Rule is { } rule ? $"   (expect {rule})" : "")}{(d.Reason.Note is { } note ? $"   (accepted: {note})" : "")}");
            }
        }
    }

    static void Footer(Report report, TextWriter w)
    {
        var s = report.Summary;
        w.WriteLine();
        if (report.Errors.Count == 0)
            w.WriteLine($"{s.ObservationsCompared} observations compared, {s.Unchanged} unchanged. {s.Unexpected} unexpected, {s.Intended} intended, {s.Accepted} accepted.");
        if (report.Run.ArtifactsDir is { } dir) w.WriteLine($"Artifacts: {dir}");
        w.WriteLine(report.ExitCode switch
        {
            0 => "No behavior differences.",
            1 => s.Unexpected > 0 ? "Behavior differs from base (unexpected differences)." : "Behavior differs from base (all differences intended).",
            _ => "BehaveDiff could not complete the comparison.",
        });
    }

    static string Describe(TreeInfo? t) =>
        t is null ? "?" : $"{t.Ref} ({t.Commit}, {t.Passed}/{t.Tests} tests passed)";

    static string Format(JsonNode? node)
    {
        var s = node is null ? "(none)" : node.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return s.Length <= MaxValueLength ? s : s[..MaxValueLength] + "…";
    }
}
