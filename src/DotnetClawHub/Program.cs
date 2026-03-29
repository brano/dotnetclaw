using DotnetClawHub.Components;
using DotnetClawHub.Data;
using DotnetClawHub.Models;
using Microsoft.EntityFrameworkCore;

// ============================================================================
//  DotnetClawHub — Skills Directory (Web API + Blazor + SQLite)
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// ── Blazor ────────────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── EF Core + SQLite ──────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                     ?? "Data Source=dotnetclawhub.db"));

var app = builder.Build();

// ── Ensure database is created ────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// ── HTTP Pipeline ─────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

// ── Minimal API — Skills ──────────────────────────────────────────────────────

// GET /api/skills?q=...
app.MapGet("/api/skills", async (string? q, AppDbContext db) =>
{
    var query = db.Skills.AsQueryable();
    if (!string.IsNullOrWhiteSpace(q))
    {
        query = query.Where(s =>
            EF.Functions.Like(s.Name, $"%{q}%") ||
            EF.Functions.Like(s.Description, $"%{q}%") ||
            EF.Functions.Like(s.Tags, $"%{q}%") ||
            EF.Functions.Like(s.Author, $"%{q}%"));
    }

    return await query
        .OrderByDescending(s => s.Downloads)
        .Select(s => new
        {
            s.Id, s.Name, s.DisplayName, s.Description,
            s.Author, s.Version, s.Tags, s.Downloads,
            s.CreatedAt, s.UpdatedAt
        })
        .ToListAsync();
});

// GET /api/skills/{name}
app.MapGet("/api/skills/{name}", async (string name, AppDbContext db) =>
{
    var skill = await db.Skills.FirstOrDefaultAsync(s => s.Name == name);
    return skill is null ? Results.NotFound() : Results.Ok(skill);
});

// GET /api/skills/{name}/SKILL.md  — raw download (increments download count)
app.MapGet("/api/skills/{name}/SKILL.md", async (string name, AppDbContext db) =>
{
    var skill = await db.Skills.FirstOrDefaultAsync(s => s.Name == name);
    if (skill is null) return Results.NotFound();
    skill.Downloads++;
    await db.SaveChangesAsync();
    return Results.Content(skill.SkillMarkdown, "text/markdown");
});

// POST /api/skills  — publish or update a skill
app.MapPost("/api/skills", async (PublishSkillRequest req, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.SkillMarkdown))
        return Results.BadRequest("Name and SkillMarkdown are required.");

    var existing = await db.Skills.FirstOrDefaultAsync(s => s.Name == req.Name);
    if (existing is not null)
    {
        existing.DisplayName = req.DisplayName;
        existing.Description = req.Description;
        existing.Author = req.Author;
        existing.Version = req.Version;
        existing.Tags = req.Tags;
        existing.SkillMarkdown = req.SkillMarkdown;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(existing);
    }

    var skill = new Skill
    {
        Name = req.Name,
        DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? req.Name : req.DisplayName,
        Description = req.Description,
        Author = req.Author,
        Version = string.IsNullOrWhiteSpace(req.Version) ? "1.0.0" : req.Version,
        Tags = req.Tags,
        SkillMarkdown = req.SkillMarkdown,
    };

    db.Skills.Add(skill);
    await db.SaveChangesAsync();
    return Results.Created($"/api/skills/{skill.Name}", skill);
});

// ── Blazor ────────────────────────────────────────────────────────────────────
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// ── Request/Response Models ───────────────────────────────────────────────────
internal sealed record PublishSkillRequest(
    string Name,
    string DisplayName,
    string Description,
    string Author,
    string Version,
    string Tags,
    string SkillMarkdown);
