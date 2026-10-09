using System.Text.Json.Nodes;
using BehaveDiff.Core;
using BehaveDiff.Core.Running;

namespace BehaveDiff.Core.Tests;

[TestClass]
public class RecordingAndConfigTests
{
    static string WriteTrx(params (string Class, string Method, string Start, string End, string Outcome)[] tests)
    {
        var ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var defs = string.Join("", tests.Select((t, i) => $"""<UnitTest id="id{i}" name="{t.Method}"><TestMethod className="My.Namespace.{t.Class}" name="{t.Method}" /></UnitTest>"""));
        var results = string.Join("", tests.Select((t, i) => $"""<UnitTestResult testId="id{i}" testName="{t.Method}" outcome="{t.Outcome}" startTime="{t.Start}" endTime="{t.End}" />"""));
        var path = Path.Combine(Path.GetTempPath(), $"bd-{Guid.NewGuid():N}.trx");
        File.WriteAllText(path, $"""<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="{ns}"><Results>{results}</Results><TestDefinitions>{defs}</TestDefinitions></TestRun>""");
        return path;
    }

    [TestMethod]
    public void Trx_NamesAreClassDotMethod_OrderedByStart()
    {
        var trx = WriteTrx(
            ("B", "Second", "2026-01-01T00:00:02+00:00", "2026-01-01T00:00:03+00:00", "Failed"),
            ("A", "First", "2026-01-01T00:00:00+00:00", "2026-01-01T00:00:01+00:00", "Passed"));
        var tests = RecordingReader.ReadTrx(trx);
        CollectionAssert.AreEqual(new[] { "A.First", "B.Second" }, tests.Select(t => t.Name).ToArray());
        Assert.AreEqual("Failed", tests[1].Outcome);
    }

    [TestMethod]
    public void OverlappingTests_AreReportedAsParallel()
    {
        var tests = RecordingReader.ReadTrx(WriteTrx(
            ("A", "One", "2026-01-01T00:00:00+00:00", "2026-01-01T00:00:02+00:00", "Passed"),
            ("A", "Two", "2026-01-01T00:00:01+00:00", "2026-01-01T00:00:03+00:00", "Passed")));
        var ex = Assert.ThrowsExactly<BehaveDiffException>(() => RecordingReader.CheckSequential("base", tests));
        StringAssert.Contains(ex.Message, "parallel");
    }

    [TestMethod]
    public void Attribution_InProcessWins_ThenTrxWindow_ThenUnattributed_PlusTestResults()
    {
        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        var tests = new List<TestResult>
        {
            new("A.One", "Passed", t0, t0.AddSeconds(1)),
            new("A.Two", "Failed", t0.AddSeconds(2), t0.AddSeconds(3)),
        };
        var raw = new List<RecordingReader.RawObservation>
        {
            new(1, "(setup)", "db-write", "(test code)", null, 1, t0.AddMilliseconds(500)),
            new(2, null, "http-response", "GET /a", null, 1, t0.AddMilliseconds(600)),
            new(3, null, "http-response", "GET /b", null, 1, t0.AddMilliseconds(2500)),
            new(4, null, "http-response", "GET /c", null, 1, t0.AddSeconds(10)),
        };
        var obs = RecordingReader.Attribute(raw, tests, null);
        CollectionAssert.AreEqual(
            new[] { "(setup)", "A.One", "A.Two", "(unattributed)", "A.One", "A.Two" },
            obs.Select(o => o.Test).ToArray());
        Assert.AreEqual("Failed", (string?)obs.Last().Data!["outcome"]);
        Assert.AreEqual(ObservationKinds.TestResult, obs.Last().Kind);
    }

    [TestMethod]
    public void Config_DefaultsAndYaml()
    {
        var c = ConfigLoader.Parse("""
            baseRef: develop
            inputs:
              - kind: mstest
                project: src/T/T.csproj
            ignore:
              jsonPaths: ["$.body.etag"]
            selfNoiseCheck: false
            """, "test");
        Assert.AreEqual("develop", c.BaseRef);
        Assert.AreEqual("src/T/T.csproj", c.Inputs.Single().Project);
        Assert.AreEqual("$.body.etag", c.Ignore.JsonPaths.Single());
        Assert.IsFalse(c.SelfNoiseCheck);
        Assert.AreEqual(100, c.LatencyRegressionPct);
        Assert.IsTrue(c.Identity.Enabled);
    }

    [TestMethod]
    public void Config_LocalFileOverridesOnlyItsKeys()
    {
        var dir = Directory.CreateTempSubdirectory("bd-config").FullName;
        File.WriteAllText(Path.Combine(dir, ConfigLoader.FileName), "baseRef: main\nlatencyRegressionPct: 50\n");
        File.WriteAllText(Path.Combine(dir, ConfigLoader.LocalFileName), "baseRef: feature\n");
        var c = ConfigLoader.Load(dir, null);
        Assert.AreEqual("feature", c.BaseRef);
        Assert.AreEqual(50, c.LatencyRegressionPct);
    }

    [TestMethod]
    public void Config_MissingExplicitFile_IsAConfigError()
    {
        var ex = Assert.ThrowsExactly<BehaveDiffException>(() => ConfigLoader.Load(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "nope.yml")));
        Assert.AreEqual("config", ex.ToRunError().Stage);
    }

    [TestMethod]
    [DataRow("**/appsettings*.json", "src/Api/appsettings.Development.json", true)]
    [DataRow("**/appsettings*.json", "appsettings.json", true)]
    [DataRow("**/appsettings*.json", "src/Api/other.json", false)]
    public void Globs(string glob, string path, bool matches) =>
        Assert.AreEqual(matches, GitRepo.GlobToRegex(glob).IsMatch(path));
}

