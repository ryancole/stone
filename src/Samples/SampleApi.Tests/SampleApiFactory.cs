using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SampleApi.Data;

namespace SampleApi.Tests;

/// <summary>One app and one fresh SQLite database per test run (WhatInBox uses Testcontainers SQL Server).</summary>
[TestClass]
public static class SampleApiFactory
{
    static readonly string DbPath = Path.Combine(Path.GetTempPath(), $"sampleapi-{Guid.NewGuid():N}.db");

    public static WebApplicationFactory<Startup> Instance { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task Init(TestContext _)
    {
        Instance = new WebApplicationFactory<Startup>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Workspace"] = $"Data Source={DbPath}",
            })));

        await using var db = await CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        // Fixture-time write, like WhatInBox seeding its test user: must be labeled setup, not the first test.
        db.Workflows.Add(new Workflow { Name = "fixture-seed", CreatedUtc = DateTime.UtcNow, ExternalId = Guid.NewGuid(), Priority = WorkflowPriority.High, Labels = ["seed", "vip"] });
        await db.SaveChangesAsync();
    }

    [AssemblyCleanup]
    public static async Task Cleanup()
    {
        await Instance.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(DbPath);
    }

    public static Task<WorkspaceContext> CreateDbContextAsync() =>
        Instance.Services.GetRequiredService<IDbContextFactory<WorkspaceContext>>().CreateDbContextAsync();
}
