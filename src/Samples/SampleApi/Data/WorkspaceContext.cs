using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace SampleApi.Data;

public enum WorkflowPriority { Low, Normal, High }

public class Workflow
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Stored as a comma-separated string; the API exposes it as a JSON array.</summary>
    public string EnabledModules { get; set; } = "";
    public bool IsArchived { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Guid ExternalId { get; set; }
    public WorkflowPriority Priority { get; set; } = WorkflowPriority.Normal;
    /// <summary>Value converter column: stored as "a;b".</summary>
    public string[] Labels { get; set; } = [];
}

public class WorkspaceContext(DbContextOptions<WorkspaceContext> options) : DbContext(options)
{
    public DbSet<Workflow> Workflows => Set<Workflow>();

    protected override void OnModelCreating(ModelBuilder model) =>
        model.Entity<Workflow>().Property(w => w.Labels).HasConversion(
            v => string.Join(';', v),
            v => v.Split(';', StringSplitOptions.RemoveEmptyEntries),
            new ValueComparer<string[]>((a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s)), v => v.ToArray()));
}

public class WorkspaceContextFactory(DbContextOptions<WorkspaceContext> options) : IDbContextFactory<WorkspaceContext>
{
    public WorkspaceContext CreateDbContext() => new(options);
}
