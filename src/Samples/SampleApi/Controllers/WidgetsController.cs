using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;

namespace SampleApi.Controllers;

public record WidgetDto(int Id, string Name, string[] Tags, bool IsArchived, DateTime CreatedUtc, Guid ExternalId)
{
    public static WidgetDto From(Widget w) => new(
        w.Id, w.Name, w.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries), w.IsArchived, w.CreatedUtc, w.ExternalId);
}

public record CreateWidgetRequest(string Name, string[]? Tags);

[ApiController]
[Route("widgets")]
public class WidgetsController(IDbContextFactory<CatalogContext> contexts) : ControllerBase
{
    [HttpGet]
    public async Task<WidgetDto[]> List()
    {
        await using var db = await contexts.CreateDbContextAsync();
        var all = await db.Widgets.OrderBy(w => w.Id).ToListAsync();
        return all.Select(WidgetDto.From).ToArray();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<WidgetDto>> Get(int id)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var w = await db.Widgets.FindAsync(id);
        return w is null ? NotFound() : WidgetDto.From(w);
    }

    [HttpPost]
    public async Task<ActionResult<WidgetDto>> Create(CreateWidgetRequest request)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var w = new Widget
        {
            Name = request.Name,
            Tags = string.Join(',', request.Tags ?? []),
            CreatedUtc = DateTime.UtcNow,
            ExternalId = Guid.NewGuid(),
        };
        db.Widgets.Add(w);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = w.Id }, WidgetDto.From(w));
    }

    [HttpPut("{id:int}/name")]
    public async Task<IActionResult> Rename(int id, [FromBody] string name)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var w = await db.Widgets.FindAsync(id);
        if (w is null) return NotFound();
        w.Name = name;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var w = await db.Widgets.FindAsync(id);
        if (w is null) return NotFound();
        db.Widgets.Remove(w);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
