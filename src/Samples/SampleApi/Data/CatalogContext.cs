using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace SampleApi.Data;

public enum WidgetPriority { Low, Normal, High }

public class Category
{
    public int Id { get; set; }
    public required string Name { get; set; }
}

public class Widget
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Stored as a comma-separated string; the API exposes it as a JSON array.</summary>
    public string Tags { get; set; } = "";
    public bool IsArchived { get; set; }
    public DateTime CreatedUtc { get; set; }
    public Guid ExternalId { get; set; }
    public WidgetPriority Priority { get; set; } = WidgetPriority.Normal;
    /// <summary>Value converter column: stored as "a;b".</summary>
    public string[] Aliases { get; set; } = [];
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
}

public class CatalogContext(DbContextOptions<CatalogContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();
    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder model) =>
        model.Entity<Widget>().Property(w => w.Aliases).HasConversion(
            v => string.Join(';', v),
            v => v.Split(';', StringSplitOptions.RemoveEmptyEntries),
            new ValueComparer<string[]>((a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s)), v => v.ToArray()));
}

public class CatalogContextFactory(DbContextOptions<CatalogContext> options) : IDbContextFactory<CatalogContext>
{
    public CatalogContext CreateDbContext() => new(options);
}
