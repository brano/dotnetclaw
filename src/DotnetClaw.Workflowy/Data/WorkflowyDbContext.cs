using DotnetClaw.Workflowy.Models;
using Microsoft.EntityFrameworkCore;

namespace DotnetClaw.Workflowy.Data;

public sealed class WorkflowyDbContext(DbContextOptions<WorkflowyDbContext> options)
    : DbContext(options)
{
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<StepResult> StepResults => Set<StepResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkflowRun>(e =>
        {
            e.HasKey(r => r.Id);
            // SQLite allows multiple NULLs in unique indexes natively, so no filter needed
            e.HasIndex(r => r.ResumeToken).IsUnique();
            e.Property(r => r.WorkflowName).IsRequired().HasMaxLength(256);
            e.Property(r => r.WorkflowPath).IsRequired().HasMaxLength(1024);
            e.Property(r => r.Status).HasConversion<string>();
            e.HasMany(r => r.StepResults)
             .WithOne(s => s.WorkflowRun)
             .HasForeignKey(s => s.WorkflowRunId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StepResult>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.WorkflowRunId, s.StepIndex });
            e.Property(s => s.Status).HasConversion<string>();
        });
    }
}
