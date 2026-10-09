using BehaveDiff.Core;
using BehaveDiff.Core.Diffing;

namespace BehaveDiff.Core.Tests;

[TestClass]
public class MatcherTests
{
    static MatchResult Match(Recording b, Recording c, double latencyPct = 100) =>
        new Matcher(new BehaveDiffConfig.IdentityConfig(), latencyPct).Match(b, c);

    [TestMethod]
    public void IdenticalRecordings_HaveNoDifferences()
    {
        var r = Match(
            TestData.Rec("base", TestData.Http("T", "GET /a", """{"status":200,"body":{"x":1}}""")),
            TestData.Rec("current", TestData.Http("T", "GET /a", """{"status":200,"body":{"x":1}}""")));
        Assert.AreEqual(0, r.Diffs.Count);
        Assert.AreEqual(1, r.PairedKeys.Count);
    }

    [TestMethod]
    public void FieldLevelChanges_AddedRemovedValueAndType()
    {
        var r = Match(
            TestData.Rec("base", TestData.Http("T", "GET /a", """{"body":{"tags":["a","b"],"gone":1,"n":1}}""")),
            TestData.Rec("current", TestData.Http("T", "GET /a", """{"body":{"tags":"a,b","new":true,"n":2}}""")));
        var byPath = r.Diffs.ToDictionary(d => d.Path!, d => d.Kind);
        Assert.AreEqual(DifferenceKind.TypeChanged, byPath["$.body.tags"]);
        Assert.AreEqual(DifferenceKind.FieldRemoved, byPath["$.body.gone"]);
        Assert.AreEqual(DifferenceKind.FieldAdded, byPath["$.body.new"]);
        Assert.AreEqual(DifferenceKind.ValueChanged, byPath["$.body.n"]);
        Assert.AreEqual(4, r.Diffs.Count);
    }

    [TestMethod]
    public void BoolFlip_IsValueChange_NotTypeChange()
    {
        var r = Match(
            TestData.Rec("base", TestData.Write("T", "POST /w", "W", "Added", """{"Id":1,"Archived":false}""")),
            TestData.Rec("current", TestData.Write("T", "POST /w", "W", "Added", """{"Id":1,"Archived":true}""")));
        Assert.AreEqual(DifferenceKind.ValueChanged, r.Diffs.Single().Kind);
        Assert.AreEqual("$.values.Archived", r.Diffs.Single().Path);
    }

    [TestMethod]
    public void IdsCompareByInsertOrder_AcrossKeysFksResponseFieldsAndPaths()
    {
        // Current inserts one extra Widget earlier, so every later id is shifted by one.
        var b = TestData.Rec("base",
            TestData.Write("T1", "POST /c", "Category", "Added", """{"Id":1}"""),
            TestData.Write("T2", "POST /w", "Widget", "Added", """{"Id":1,"CategoryId":1}""", """{"CategoryId":"Category"}"""),
            TestData.Http("T2", "GET /w/{id}", """{"request":{"path":"/w/1"},"body":{"id":1,"categoryId":1}}"""));
        var c = TestData.Rec("current",
            TestData.Write("T0", "POST /c", "Category", "Added", """{"Id":1}"""),
            TestData.Write("T1", "POST /c", "Category", "Added", """{"Id":2}"""),
            TestData.Write("T2", "POST /w", "Widget", "Added", """{"Id":1,"CategoryId":2}""", """{"CategoryId":"Category"}"""),
            TestData.Http("T2", "GET /w/{id}", """{"request":{"path":"/w/1"},"body":{"id":1,"categoryId":2}}"""));
        var r = Match(b, c);

        // T1's category is the 1st in base and the 2nd in current: a real difference in which row it is.
        // The FK and the response field follow the same rule.
        var t2 = r.Diffs.Where(d => d.Key.Test == "T2").Select(d => d.Path).ToList();
        CollectionAssert.AreEquivalent(new[] { "$.values.CategoryId", "$.body.categoryId" }, t2);
    }

    [TestMethod]
    public void ShiftedIdentityValues_AreEqualWhenSameRowByOrder()
    {
        var b = TestData.Rec("base",
            TestData.Write("T1", "POST /w", "Widget", "Added", """{"Id":7}"""),
            TestData.Http("T1", "GET /w/{id}", """{"request":{"path":"/w/7"},"body":{"id":7}}"""));
        var c = TestData.Rec("current",
            TestData.Write("T1", "POST /w", "Widget", "Added", """{"Id":8}"""),
            TestData.Http("T1", "GET /w/{id}", """{"request":{"path":"/w/8"},"body":{"id":8}}"""));
        Assert.AreEqual(0, Match(b, c).Diffs.Count);
    }

    [TestMethod]
    public void IdentityDisabled_ComparesRawValues()
    {
        var b = TestData.Rec("base", TestData.Write("T1", "POST /w", "Widget", "Added", """{"Id":7}"""));
        var c = TestData.Rec("current", TestData.Write("T1", "POST /w", "Widget", "Added", """{"Id":8}"""));
        var r = new Matcher(new BehaveDiffConfig.IdentityConfig { Enabled = false }, 100).Match(b, c);
        Assert.AreEqual("$.values.Id", r.Diffs.Single().Path);
    }

    [TestMethod]
    public void ExtraObservationInGroup_IsOneSided_AndDoesNotShiftThePairs()
    {
        var b = TestData.Rec("base",
            TestData.Http("T", "POST /a", """{"body":{"n":"one"}}"""),
            TestData.Http("T", "POST /a", """{"body":{"n":"two"}}"""));
        var c = TestData.Rec("current",
            TestData.Http("T", "POST /a", """{"body":{"n":"extra"}}"""),
            TestData.Http("T", "POST /a", """{"body":{"n":"one"}}"""),
            TestData.Http("T", "POST /a", """{"body":{"n":"two"}}"""));
        var d = Match(b, c).Diffs.Single();
        Assert.AreEqual(DifferenceKind.ObservationAdded, d.Kind);
        Assert.AreEqual("extra", (string?)d.After!["body"]!["n"]);
    }

    [TestMethod]
    public void SameObservationInDifferentTest_IsMoved_NotADifference()
    {
        var seed = """{"Id":1,"Name":"seed"}""";
        var r = Match(
            TestData.Rec("base", TestData.Write("TestA", "(test code)", "User", "Added", seed)),
            TestData.Rec("current", TestData.Write("TestB", "(test code)", "User", "Added", seed)));
        Assert.AreEqual(0, r.Diffs.Count);
        Assert.AreEqual("TestA", r.Moved.Single().Base.Test);
        Assert.AreEqual("TestB", r.Moved.Single().Current.Test);
    }

    [TestMethod]
    public void MissingObservation_IsRemoved()
    {
        var r = Match(
            TestData.Rec("base", TestData.Write("T", "POST /a", "Audit", "Added", """{"Id":1}""")),
            TestData.Rec("current"));
        Assert.AreEqual(DifferenceKind.ObservationRemoved, r.Diffs.Single().Kind);
        Assert.AreEqual("Audit", r.Diffs.Single().Key.Entity);
    }

    [TestMethod]
    public void Latency_FlagsOnlyLargeRegressions()
    {
        var r = Match(
            TestData.Rec("base", TestData.Http("T", "GET /slow", "{}", 10), TestData.Http("T", "GET /jitter", "{}", 2)),
            TestData.Rec("current", TestData.Http("T", "GET /slow", "{}", 80), TestData.Http("T", "GET /jitter", "{}", 9)));
        Assert.AreEqual("GET /slow", r.Latency.Single().Match.Trigger);
    }

    [TestMethod]
    public void Align_PairsGapsInOrder()
    {
        var pairs = Matcher.Align(["a", "x", "c"], ["a", "y", "c"], (p, q) => p == q).ToList();
        CollectionAssert.AreEqual(new (int?, int?)[] { (0, 0), (1, 1), (2, 2) }, pairs);
    }
}
