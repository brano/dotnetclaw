using DotnetClawHub.Models;
using Microsoft.EntityFrameworkCore;

namespace DotnetClawHub.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Skill> Skills => Set<Skill>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Skill>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.Name).IsRequired().HasMaxLength(100);
            e.Property(s => s.DisplayName).HasMaxLength(200);
            e.Property(s => s.Description).HasMaxLength(1000);
            e.Property(s => s.Author).HasMaxLength(100);
            e.Property(s => s.Version).HasMaxLength(50);
            e.Property(s => s.Tags).HasMaxLength(500);
        });
    }
}
