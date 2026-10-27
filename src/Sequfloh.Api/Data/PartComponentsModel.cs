using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.MasterData;

namespace Sequfloh.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<PartComponentLink> PartComponentLinks => Set<PartComponentLink>();

    private static void ConfigurePartComponents(ModelBuilder b)
    {
        b.Entity<PartComponentLink>(e =>
        {
            e.ToTable("part_component_links");
            e.HasKey(x => new { x.PartId, x.Kind, x.ComponentPartId });
            e.HasIndex(x => x.ComponentPartId);
            e.Property(x => x.CreatedBy).HasMaxLength(200);
            e.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Part>().WithMany().HasForeignKey(x => x.ComponentPartId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
