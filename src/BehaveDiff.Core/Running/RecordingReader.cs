using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace BehaveDiff.Core.Running;

/// <summary>Turns capture JSONL + a TRX report into a <see cref="Recording"/>.</summary>
static class RecordingReader
{
    public static Recording Read(string tree, string reference, string commit, string captureDir, string trxPath, string? projectPrefix = null)
    {
        var tests = ReadTrx(trxPath);
        CheckSequential(tree, tests);
        var raw = ReadObservations(captureDir);
        var observations = Attribute(raw, tests, projectPrefix);
        return new Recording(tree, reference, commit, Prefix(tests, projectPrefix), observations);
    }

    static IReadOnlyList<TestResult> Prefix(IReadOnlyList<TestResult> tests, string? prefix) =>
        prefix is null ? tests : tests.Select(t => t with { Name = $"{prefix}:{t.Name}" }).ToList();

    /// <summary>"Class.Method" names, start/end/outcome, ordered by start.</summary>
    internal static IReadOnlyList<TestResult> ReadTrx(string trxPath)
    {
        var doc = XDocument.Load(trxPath);
        XNamespace ns = doc.Root!.Name.Namespace;

        var classByTestId = doc.Descendants(ns + "UnitTest")
            .Select(u => (Id: (string?)u.Attribute("id"), Class: (string?)u.Element(ns + "TestMethod")?.Attribute("className")))
            .Where(x => x.Id is not null)
            .ToDictionary(x => x.Id!, x => x.Class);

        return doc.Descendants(ns + "UnitTestResult")
            .Select(r =>
            {
                var name = (string)r.Attribute("testName")!;
                var cls = classByTestId.GetValueOrDefault((string?)r.Attribute("testId") ?? "");
                var shortClass = cls?.Split('.')[^1];
                return new TestResult(
                    shortClass is null || name.StartsWith(shortClass + ".") ? name : $"{shortClass}.{name}",
                    (string?)r.Attribute("outcome") ?? "Unknown",
                    DateTimeOffset.Parse((string)r.Attribute("startTime")!),
                    DateTimeOffset.Parse((string)r.Attribute("endTime")!));
            })
            .OrderBy(t => t.Start)
            .ToList();
    }

    /// <summary>Attribution by time window only holds if tests ran one at a time.</summary>
    internal static void CheckSequential(string tree, IReadOnlyList<TestResult> tests)
    {
        for (var i = 1; i < tests.Count; i++)
        {
            // 1 ms slack: start/end are stamped independently.
            if (tests[i].Start < tests[i - 1].End.AddMilliseconds(-1))
                throw new BehaveDiffException(tree, "attribution",
                    $"Tests ran in parallel ('{tests[i - 1].Name}' and '{tests[i].Name}' overlap); BehaveDiff needs sequential tests to attribute observations. Disable test parallelization for this project.");
        }
    }

    internal sealed record RawObservation(long Seq, string? Test, string Kind, string Trigger, JsonNode? Data, double? DurationMs, DateTimeOffset Timestamp);

    static List<RawObservation> ReadObservations(string captureDir)
    {
        var result = new List<RawObservation>();
        if (!Directory.Exists(captureDir)) return result;
        // One file per process; only the test host writes observations.
        foreach (var file in Directory.EnumerateFiles(captureDir, "observations-*.jsonl"))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var o = JsonNode.Parse(line)!.AsObject();
                result.Add(new RawObservation(
                    (long)o["seq"]!,
                    (string?)o["test"],
                    (string)o["kind"]!,
                    (string)o["trigger"]!,
                    o["data"]?.DeepClone(),
                    (double?)o["durationMs"],
                    DateTimeOffset.Parse((string)o["ts"]!)));
            }
        }
        return result.OrderBy(o => o.Seq).ToList();
    }

    /// <summary>
    /// In-process attribution from the hook wins (it saw the running test, or fixture code);
    /// otherwise the TRX window containing the timestamp. Adds one test-result observation per test.
    /// </summary>
    internal static IReadOnlyList<Observation> Attribute(IReadOnlyList<RawObservation> raw, IReadOnlyList<TestResult> tests, string? prefix)
    {
        string Name(string test) => prefix is null || test.StartsWith('(') ? test : $"{prefix}:{test}";

        var result = new List<Observation>(raw.Count + tests.Count);
        foreach (var o in raw)
        {
            var test = o.Test
                ?? tests.FirstOrDefault(t => t.Start <= o.Timestamp && o.Timestamp <= t.End)?.Name
                ?? SpecialTests.Unattributed;
            result.Add(new Observation(o.Seq, Name(test), o.Kind, o.Trigger, o.Data, o.DurationMs, o.Timestamp));
        }

        var seq = raw.Count == 0 ? 0 : raw.Max(o => o.Seq);
        foreach (var t in tests)
            result.Add(new Observation(++seq, Name(t.Name), ObservationKinds.TestResult, "(test)",
                new JsonObject { ["outcome"] = t.Outcome }, (t.End - t.Start).TotalMilliseconds, t.End));
        return result;
    }
}
