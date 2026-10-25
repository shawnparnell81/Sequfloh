using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.Imports;

namespace Sequfloh.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<ImportedFile> ImportedFiles => Set<ImportedFile>();
    public DbSet<ImportedFileVersion> ImportedFileVersions => Set<ImportedFileVersion>();

    private static void ConfigureImports(ModelBuilder b)
    {
        b.Entity<ImportedFile>(e =>
        {
            e.ToTable("imported_files");
            e.Property(x => x.Number).HasMaxLength(300);
            e.HasIndex(x => x.Number).IsUnique();
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.SourceFormat).HasMaxLength(10);
            e.Property(x => x.OriginalFileName).HasMaxLength(255);
            e.Property(x => x.OriginalStorageKey).HasMaxLength(300);
            e.Property(x => x.Summary).HasColumnType("jsonb");
            e.HasIndex(x => new { x.SiteId, x.DepartmentId });
            e.HasIndex(x => x.BlankFormId);
            e.Property(x => x.PartNumber).HasMaxLength(100);
            e.HasOne<ImportedFile>().WithMany().HasForeignKey(x => x.BlankFormId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(v => v.ImportedFileId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ImportedFileVersion>(e =>
        {
            e.ToTable("imported_file_versions");
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(150);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.CreatedByName).HasMaxLength(200);
            e.HasIndex(x => new { x.ImportedFileId, x.Version }).IsUnique();
        });
    }
}
