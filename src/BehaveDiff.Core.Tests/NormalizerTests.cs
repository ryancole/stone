using System.Text.Json.Nodes;
using BehaveDiff.Core;
using BehaveDiff.Core.Diffing;

namespace BehaveDiff.Core.Tests;

[TestClass]
public class NormalizerTests
{
    [TestMethod]
    [DataRow("wd-create-53359186d3ee4ad1accd4a95dd8d3617", "wd-create-<guid>")]
    [DataRow("1c61b601-c2e3-450a-93c9-aaeb6a08e8e6", "<guid>")]
    [DataRow("id 1C61B601-C2E3-450A-93C9-AAEB6A08E8E6 here", "id <guid> here")]
    [DataRow("2026-10-09T07:17:08.7112344Z", "<timestamp>")]
    [DataRow("2026-10-09T07:17:08.7112344", "<timestamp>")]
    [DataRow("2026-10-09T07:17:08.07", "<timestamp>")]
    [DataRow("2026-10-09T02:17:08.6067918-05:00", "<timestamp>")]
    [DataRow("0001-01-01T00:00:00", "<timestamp>")]
    [DataRow("00-881faa6e96410ea3f228fe5b0779d91e-3162a2917310be8f-00", "<traceid>")]
    [DataRow("plain text", "plain text")]
    [DataRow("2026-10-09", "2026-10-09")]
    public void ScrubString(string input, string expected) =>
        Assert.AreEqual(expected, Normalizer.ScrubString(input));

    [TestMethod]
    public void Http_DropsConfiguredHeadersCaseInsensitively_AndContentLengthWhenBodyCaptured()
    {
        var n = new Normalizer(new BehaveDiffConfig.IgnoreConfig { Headers = ["date"] });
        var o = n.Normalize(TestData.Http("T", "GET /x", """{"headers":{"Date":"x","Content-Length":"12","Content-Type":"a"},"body":{"a":1}}"""));
        var headers = o.Data!["headers"]!.AsObject();
        CollectionAssert.AreEquivalent(new[] { "Content-Type" }, headers.Select(h => h.Key).ToArray());
    }

    [TestMethod]
    public void Http_KeepsContentLengthWithoutBody()
    {
        var n = new Normalizer(new BehaveDiffConfig.IgnoreConfig { Headers = [] });
        var o = n.Normalize(TestData.Http("T", "GET /x", """{"headers":{"Content-Length":"0"},"body":null}"""));
        Assert.IsNotNull(o.Data!["headers"]!["Content-Length"]);
    }

    [TestMethod]
    public void DbWrite_DropsColumnsByNameOrEntityQualified()
    {
        var n = new Normalizer(new BehaveDiffConfig.IgnoreConfig { DbColumns = ["Stamp", "Widget.Secret"] });
        var o = n.Normalize(TestData.Write("T", "POST /w", "Widget", "Added", """{"Id":1,"Stamp":"x","Secret":"y","Name":"n"}"""));
        CollectionAssert.AreEquivalent(new[] { "Id", "Name" }, o.Data!["values"]!.AsObject().Select(v => v.Key).ToArray());
    }

    [TestMethod]
    public void ScrubsNestedStringsAndTriggers_WithoutTouchingTheOriginal()
    {
        var original = TestData.Http("T", "GET /files/53359186d3ee4ad1accd4a95dd8d3617", """{"body":{"items":[{"id":"1c61b601-c2e3-450a-93c9-aaeb6a08e8e6"}]}}""");
        var o = new Normalizer(new()).Normalize(original);
        Assert.AreEqual("<guid>", (string?)o.Data!["body"]!["items"]![0]!["id"]);
        Assert.AreEqual("GET /files/<guid>", o.Trigger);
        Assert.AreEqual("1c61b601-c2e3-450a-93c9-aaeb6a08e8e6", (string?)original.Data!["body"]!["items"]![0]!["id"]);
    }
}
