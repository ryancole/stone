using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;

namespace SampleApi.Controllers;

public record WorkflowDto(int Id, string Name, string[] EnabledModules, bool IsArchived, DateTime CreatedUtc, Guid ExternalId)
{
    public static WorkflowDto From(Workflow w) => new(
        w.Id, w.Name, w.EnabledModules.Split(',', StringSplitOptions.RemoveEmptyEntries), w.IsArchived, w.CreatedUtc, w.ExternalId);
}

public record CreateWorkflowRequest(string Name, string[]? EnabledModules);

[ApiController]
[Route("workflows")]
public class WorkflowsController(IDbContextFactory<WorkspaceContext> contexts) : ControllerBase
{
    [HttpGet]
    public async Task<WorkflowDto[]> List()
    {
        await using var db = await contexts.CreateDbContextAsync();
        var all = await db.Workflows.OrderBy(w => w.Id).ToListAsync();
        return all.Select(WorkflowDto.From).ToArray();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<WorkflowDto>> Get(int id)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var w = await db.Workflows.FindAsync(id);
        return w is null ? NotFound() : WorkflowDto.From(w);
    }

    [HttpPost]
    public async Task<ActionResult<WorkflowDto>> Create(CreateWorkflowRequest request)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var w = new Workflow
        {
            Name = request.Name,
            EnabledModules = string.Join(',', request.EnabledModules ?? []),
            CreatedUtc = DateTime.UtcNow,
            ExternalId = Guid.NewGuid(),
        };
        db.Workflows.Add(w);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = w.Id }, WorkflowDto.From(w));
    }

    [HttpPut("{id:int}/name")]
    public async Task<IActionResult> Rename(int id, [FromBody] string name)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var w = await db.Workflows.FindAsync(id);
        if (w is null) return NotFound();
        w.Name = name;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await using var db = await contexts.CreateDbContextAsync();
        var w = await db.Workflows.FindAsync(id);
        if (w is null) return NotFound();
        db.Workflows.Remove(w);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
