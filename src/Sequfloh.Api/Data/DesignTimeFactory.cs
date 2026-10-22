using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sequfloh.Infrastructure.Persistence;

/// <summary>Used only by the migration tooling; no database is contacted to build migrations.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=sequfloh_design", n => n.MigrationsHistoryTable("__ef_migrations", AppDbContext.Schema)).Options);
}
