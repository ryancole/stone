using System.Net;
using System.Net.Http.Json;
using Newtonsoft.Json.Linq;
using SampleApi.Data;

namespace SampleApi.Tests;

[TestClass]
public class WidgetEndpointTests
{
    static HttpClient Client => SampleApiFactory.Instance.CreateClient();

    static string UniqueName(string label) => $"wd-{label}-{Guid.NewGuid():N}";

    static async Task<JObject> CreateAsync(string label, params string[] tags)
    {
        var response = await Client.PostAsJsonAsync("/widgets", new { name = UniqueName(label), tags = tags });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task CreateWidget_ReturnsCreated()
    {
        var created = await CreateAsync("create", "red");
        Assert.IsTrue(created["id"]!.Value<int>() > 0);
    }

    [TestMethod]
    public async Task GetWidget_TagsSerializeAsJsonArray()
    {
        var created = await CreateAsync("tags", "red", "blue");
        var body = JObject.Parse(await Client.GetStringAsync($"/widgets/{created["id"]}"));
        Assert.AreEqual(JTokenType.Array, body["tags"]!.Type);
    }

    [TestMethod]
    public async Task RenameWidget_ChangesName()
    {
        var created = await CreateAsync("rename");
        var response = await Client.PutAsJsonAsync($"/widgets/{created["id"]}/name", UniqueName("renamed"));
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteWidget_ThenGetReturnsNotFound()
    {
        var created = await CreateAsync("delete");
        await Client.DeleteAsync($"/widgets/{created["id"]}");
        var response = await Client.GetAsync($"/widgets/{created["id"]}");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task SeededDirectly_IsListed()
    {
        // A write from test code, outside any request: exercises the "(test code)" trigger.
        await using (var db = await SampleApiFactory.CreateDbContextAsync())
        {
            db.Widgets.Add(new Widget { Name = UniqueName("seeded"), CreatedUtc = DateTime.UtcNow, ExternalId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }
        var list = JArray.Parse(await Client.GetStringAsync("/widgets"));
        Assert.IsTrue(list.Any(w => w["name"]!.Value<string>()!.StartsWith("wd-seeded-")));
    }

    [TestMethod]
    public async Task Health_ReturnsOk()
    {
        var body = JObject.Parse(await Client.GetStringAsync("/health"));
        Assert.AreEqual("ok", body["status"]!.Value<string>());
    }
}
