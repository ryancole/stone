using BehaveDiff.Core;
using BehaveDiff.Core.Classify;
using BehaveDiff.Core.Diffing;

namespace BehaveDiff.Core.Tests;

[TestClass]
public class ClassifierAndRulesTests
{
    static DiffItem Item(string trigger, string? entity = null, string? path = "$.values.X", string kind = ObservationKinds.DbWrite) =>
        new(new MatchKey("T", kind, trigger, entity, 0), DifferenceKind.ValueChanged, path, null, null, null, null);

    [TestMethod]
    [DataRow("POST /widgets", "POST /widgets", true)]
    [DataRow("post  /widgets/", "POST /widgets", true)]
    [DataRow("GET /widgets/{id}", "GET /widgets/{id:int}", true)]
    [DataRow("GET /widgets/*", "GET /widgets/{id:int}/name", true)]
    [DataRow("GET /widgets", "GET /widgets/{id}", false)]
    public void ExpectTrigger(string expect, string trigger, bool matches) =>
        Assert.AreEqual(matches, Classifier.Matches(expect, Item(trigger)));

    [TestMethod]
    public void ExpectEntity_MatchesDbWritesOfThatEntityOnly()
    {
        Assert.IsTrue(Classifier.Matches("entity:widget", Item("POST /widgets", "Widget")));
        Assert.IsFalse(Classifier.Matches("entity:Widget", Item("GET /widgets", null, kind: ObservationKinds.HttpResponse)));
    }

    [TestMethod]
    public void Classify_AcceptedBeatsExpect_ExpectBeatsDefault()
    {
        var item = Item("POST /widgets", "Widget");
        var id = Classifier.StableId(item);
        var none = new Dictionary<string, AcceptedDifference>();
        Assert.AreEqual(Classification.Unexpected, Classifier.Classify(item, [], none).Classification);

        var intended = Classifier.Classify(item, ["entity:Widget"], none);
        Assert.AreEqual(Classification.Intended, intended.Classification);
        Assert.AreEqual("entity:Widget", intended.Reason.Rule);

        var accepted = Classifier.Classify(item, ["entity:Widget"], new Dictionary<string, AcceptedDifference> { [id] = new(id, "known", DateTimeOffset.UtcNow) });
        Assert.AreEqual(Classification.Accepted, accepted.Classification);
        Assert.AreEqual("known", accepted.Reason.Note);
    }

    [TestMethod]
    public void StableId_DependsOnWhatAndWhere_NotOnValues()
    {
        var a = Item("POST /widgets", "Widget") with { Before = 1, After = 2 };
        var b = Item("POST /widgets", "Widget") with { Before = 5, After = 9 };
        Assert.AreEqual(Classifier.StableId(a), Classifier.StableId(b));
        Assert.AreNotEqual(Classifier.StableId(a), Classifier.StableId(a with { Path = "$.values.Y" }));
        StringAssert.StartsWith(Classifier.StableId(a), "d-");
    }

    [TestMethod]
    [DataRow("$.body.items[*].etag", "$.body.items[3].etag", true)]
    [DataRow("$.body.items[*].etag", "$.body.items[3].name", false)]
    [DataRow("$.body.items", "$.body.items[3].etag", true)]
    [DataRow("$.body.item", "$.body.items", false)]
    [DataRow("$.headers.*", "$.headers.X-Trace", true)]
    [DataRow("$", null, true)]
    [DataRow("$.body", null, false)]
    public void PathPatterns(string pattern, string? path, bool matches) =>
        Assert.AreEqual(matches, PathPattern.Matches(pattern, path));

    [TestMethod]
    public void SelfNoiseRules_GeneralizeIndices_AndSuppressMatchingDiffs()
    {
        var noisy = Item("GET /w", path: "$.body[2].lastSeen", kind: ObservationKinds.HttpResponse);
        var rules = BehaveDiffRunner.SelfNoiseRules(new MatchResult([noisy, noisy], [], [], [])).ToList();
        Assert.AreEqual("$.body[*].lastSeen", rules.Single().Path);

        Assert.IsTrue(BehaveDiffRunner.Suppresses(rules[0], Item("GET /w", path: "$.body[0].lastSeen", kind: ObservationKinds.HttpResponse)));
        Assert.IsFalse(BehaveDiffRunner.Suppresses(rules[0], Item("GET /other", path: "$.body[0].lastSeen", kind: ObservationKinds.HttpResponse)));
        Assert.IsTrue(BehaveDiffRunner.Suppresses(new NoiseRule("*", "*", "$.body[*].lastSeen", "config"), Item("GET /other", path: "$.body[0].lastSeen")));
    }

    [TestMethod]
    public void ExitCodes()
    {
        static Report With(IReadOnlyList<Difference> diffs, IReadOnlyList<RunError>? errors = null) =>
            new(1, new RunInfo(null, null, [], DateTimeOffset.UtcNow, 0, null), new IntentInfo(null, []),
                new ReportSummary(0, 0, 0, 0, 0, 0, 0, 0), diffs, [], [], [], errors ?? []);
        Difference D(Classification c) => Classifier.Classify(Item("POST /w"), [], new Dictionary<string, AcceptedDifference>()) with { Classification = c };

        Assert.AreEqual(0, With([]).ExitCode);
        Assert.AreEqual(0, With([D(Classification.Accepted)]).ExitCode);
        Assert.AreEqual(1, With([D(Classification.Intended)]).ExitCode);
        Assert.AreEqual(1, With([D(Classification.Unexpected)]).ExitCode);
        Assert.AreEqual(2, With([D(Classification.Unexpected)], [new RunError("current", "build", "x", null)]).ExitCode);
    }
}
