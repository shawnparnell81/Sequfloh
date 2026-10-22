using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Common;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Organization;
using Sequfloh.Domain.Security;
using Sequfloh.Domain.Shared;
using Sequfloh.Infrastructure.Identity;
using Sequfloh.Infrastructure.Services;

namespace Sequfloh.Infrastructure.Persistence;

public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public const string Schema = "sqf";

    public DbSet<ApplicationUser> Users => Set<ApplicationUser>();
    public DbSet<OrganizationSettings> OrganizationSettings => Set<OrganizationSettings>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Role> QmsRoles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRoleAssignment> UserRoleAssignments => Set<UserRoleAssignment>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<PartRevision> PartRevisions => Set<PartRevision>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasDefaultSchema(Schema);
        ConfigureSequfloh(b);
        ConfigureImports(b);
        ConfigureDrawings(b);

        foreach (var et in b.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
            b.Entity(et.ClrType).Property(nameof(Entity.RowVersion)).IsRowVersion();
        // Ids are assigned in code (UUIDv7); tell EF so new children added through navigations are inserted, not updated.
        foreach (var et in b.Model.GetEntityTypes())
        {
            var pk = et.FindPrimaryKey();
            if (pk is { Properties.Count: 1 } && pk.Properties[0].ClrType == typeof(Guid) && pk.Properties[0].Name == "Id")
                pk.Properties[0].ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }

        b.Entity<ApplicationUser>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.UserName).HasMaxLength(256).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.NormalizedEmail).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.JobTitle).HasMaxLength(200);
            e.Property(x => x.PasswordHash).HasMaxLength(500);
        });
        b.Entity<OrganizationSettings>(e =>
        {
            e.ToTable("organization_settings");
            e.Property(x => x.ApplicationName).HasMaxLength(100).IsRequired();
            e.Property(x => x.OrganizationName).HasMaxLength(200).IsRequired();
            e.Property(x => x.TimeZone).HasMaxLength(100);
        });
        b.Entity<Site>(e =>
        {
            e.ToTable("sites");
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TimeZone).HasMaxLength(100);
            e.HasIndex(x => x.Code).IsUnique();
        });
        b.Entity<Department>(e =>
        {
            e.ToTable("departments");
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.SiteId, x.Code }).IsUnique();
            e.HasOne(x => x.Site).WithMany(s => s.Departments).HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(x => new { x.RoleId, x.Permission });
            e.Property(x => x.Permission).HasMaxLength(100);
        });
        b.Entity<UserRoleAssignment>(e =>
        {
            e.ToTable("user_role_assignments");
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.UserId, x.RoleId, x.SiteId, x.DepartmentId }).IsUnique().AreNullsDistinct(false);
        });
        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_entries");
            e.Property(x => x.UserName).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(60);
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Changes).HasColumnType("jsonb");
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => x.UserId);
        });
        b.Entity<NumberSequence>(e =>
        {
            e.ToTable("number_sequences");
            e.Property(x => x.RecordType).HasMaxLength(60);
            e.Property(x => x.Prefix).HasMaxLength(20);
            e.HasIndex(x => new { x.RecordType, x.SiteId }).IsUnique().AreNullsDistinct(false);
        });
        b.Entity<Attachment>(e =>
        {
            e.ToTable("attachments");
            e.Property(x => x.OwnerType).HasMaxLength(60);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(150);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.HasIndex(x => new { x.OwnerType, x.OwnerId });
        });
        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Body).HasMaxLength(2000);
            e.Property(x => x.RecordType).HasMaxLength(60);
            e.Property(x => x.Link).HasMaxLength(400);
            e.HasIndex(x => new { x.UserId, x.ReadAt });
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Part>(e =>
        {
            e.ToTable("parts");
            e.Property(x => x.PartNumber).HasMaxLength(60).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Revision).HasMaxLength(10);
            e.Property(x => x.UnitOfMeasure).HasMaxLength(20);
            e.Property(x => x.Material).HasMaxLength(200);
            e.HasIndex(x => x.PartNumber).IsUnique();
            e.HasOne<Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<PartRevision>(e =>
        {
            e.ToTable("part_revisions");
            e.Property(x => x.Revision).HasMaxLength(10);
            e.Property(x => x.Reason).HasMaxLength(1000);
            e.HasIndex(x => new { x.PartId, x.Revision }).IsUnique();
            e.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
