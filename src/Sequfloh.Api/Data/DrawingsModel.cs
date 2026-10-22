using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.Drawings;

namespace Sequfloh.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<DrawingFolder> DrawingFolders => Set<DrawingFolder>();
    public DbSet<Drawing> Drawings => Set<Drawing>();
    public DbSet<DrawingRevision> DrawingRevisions => Set<DrawingRevision>();
    public DbSet<DrawingPartLink> DrawingPartLinks => Set<DrawingPartLink>();
    public DbSet<TrackingRecordDrawing> TrackingRecordDrawings => Set<TrackingRecordDrawing>();

    private static void ConfigureDrawings(ModelBuilder b)
    {
        b.Entity<DrawingFolder>(e =>
        {
            e.ToTable("drawing_folders");
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => x.PartId);
            e.HasIndex(x => new { x.SiteId, x.DepartmentId });
            e.HasOne<DrawingFolder>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Sequfloh.Domain.MasterData.Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<Drawing>(e =>
        {
            e.ToTable("drawings");
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.DrawingNumber).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.CurrentRevision).HasMaxLength(20);
            e.HasIndex(x => x.FolderId);
            e.HasIndex(x => x.DrawingNumber);
            e.HasOne<DrawingFolder>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<DrawingPartLink>(e =>
        {
            e.ToTable("drawing_part_links");
            e.HasKey(x => new { x.DrawingId, x.PartId });
            e.HasIndex(x => x.PartId);
            e.HasOne<Drawing>().WithMany().HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Sequfloh.Domain.MasterData.Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<TrackingRecordDrawing>(e =>
        {
            e.ToTable("tracking_record_drawings");
            e.HasKey(x => new { x.TrackingRecordId, x.DrawingId });
            e.Property(x => x.AttachedRevision).HasMaxLength(20);
            e.HasOne<Drawing>().WithMany().HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Sequfloh.Domain.Execution.SequflohTrackingRecord>().WithMany().HasForeignKey(x => x.TrackingRecordId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DrawingRevision>(e =>
        {
            e.ToTable("drawing_revisions");
            e.Property(x => x.Revision).HasMaxLength(20);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(150);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.CreatedByName).HasMaxLength(200);
            e.HasIndex(x => new { x.DrawingId, x.Revision }).IsUnique();
            e.HasOne<Drawing>().WithMany().HasForeignKey(x => x.DrawingId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
