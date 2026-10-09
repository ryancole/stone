using Microsoft.EntityFrameworkCore;
using SampleApi.Data;

namespace SampleApi;

public class Startup(IConfiguration configuration)
{
    public void ConfigureServices(IServiceCollection services)
    {
        // Mirrors the target app: a factory registration with a custom factory type, not AddDbContext.
        services.AddDbContextFactory<CatalogContext, CatalogContextFactory>(options =>
            options.UseSqlite(configuration.GetConnectionString("Catalog")));

        services.AddControllers().AddNewtonsoftJson();
    }

    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            // Minimal API with System.Text.Json, to exercise the PipeWriter response path.
            endpoints.MapGet("/health", () => Results.Json(new { status = "ok", checkedAt = DateTimeOffset.UtcNow }));
        });
    }
}
