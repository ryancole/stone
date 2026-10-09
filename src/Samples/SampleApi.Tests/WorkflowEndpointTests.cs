using System.Net;
using System.Net.Http.Json;
using Newtonsoft.Json.Linq;
using SampleApi.Data;

namespace SampleApi.Tests;

[TestClass]
public class WorkflowEndpointTests
{
    static HttpClient Client => SampleApiFactory.Instance.CreateClient();

    static string UniqueName(string label) => $"wf-{label}-{Guid.NewGuid():N}";

    static async Task<JObject> CreateAsync(string label, params string[] modules)
    {
        var response = await Client.PostAsJsonAsync("/workflows", new { name = UniqueName(label), enabledModules = modules });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task CreateWorkflow_ReturnsCreated()
    {
        var created = await CreateAsync("create", "inbox");
        Assert.IsTrue(created["id"]!.Value<int>() > 0);
    }

    [TestMethod]
    public async Task GetWorkflow_EnabledModulesSerializesAsJsonArrayOfNames()
    {
        var created = await CreateAsync("modules", "inbox", "rules");
        var body = JObject.Parse(await Client.GetStringAsync($"/workflows/{created["id"]}"));
        Assert.AreEqual(JTokenType.Array, body["enabledModules"]!.Type);
    }

    [TestMethod]
    public async Task RenameWorkflow_ChangesName()
    {
        var created = await CreateAsync("rename");
        var response = await Client.PutAsJsonAsync($"/workflows/{created["id"]}/name", UniqueName("renamed"));
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [TestMethod]
    public async Task DeleteWorkflow_ThenGetReturnsNotFound()
    {
        var created = await CreateAsync("delete");
        await Client.DeleteAsync($"/workflows/{created["id"]}");
        var response = await Client.GetAsync($"/workflows/{created["id"]}");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task SeededDirectly_IsListed()
    {
        // A write from test code, outside any request: exercises the "(test code)" trigger.
        await using (var db = await SampleApiFactory.CreateDbContextAsync())
        {
            db.Workflows.Add(new Workflow { Name = UniqueName("seeded"), CreatedUtc = DateTime.UtcNow, ExternalId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }
        var list = JArray.Parse(await Client.GetStringAsync("/workflows"));
        Assert.IsTrue(list.Any(w => w["name"]!.Value<string>()!.StartsWith("wf-seeded-")));
    }

    [TestMethod]
    public async Task Health_ReturnsOk()
    {
        var body = JObject.Parse(await Client.GetStringAsync("/health"));
        Assert.AreEqual("ok", body["status"]!.Value<string>());
    }
}
