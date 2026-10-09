using Microsoft.EntityFrameworkCore;

namespace SampleApi.Data;

public class Workflow
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Stored as a comma-separated string; the API exposes it as a JSON array.</summary>
    public string EnabledModules { get; set; } = "";
    public bool IsArchived { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Guid ExternalId { get; set; }
}

public class WorkspaceContext(DbContextOptions<WorkspaceContext> options) : DbContext(options)
{
    public DbSet<Workflow> Workflows => Set<Workflow>();
}

public class WorkspaceContextFactory(DbContextOptions<WorkspaceContext> options) : IDbContextFactory<WorkspaceContext>
{
    public WorkspaceContext CreateDbContext() => new(options);
}
