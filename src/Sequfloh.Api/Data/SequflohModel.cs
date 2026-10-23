using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.Execution;
using Sequfloh.Domain.MasterData;
using Sequfloh.Infrastructure.Identity;

namespace Sequfloh.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<SequflohSettings> SequflohSettings => Set<SequflohSettings>();
    public DbSet<SequflohReferenceValue> SequflohReferenceValues => Set<SequflohReferenceValue>();
    public DbSet<SequflohUserItem> SequflohUserItems => Set<SequflohUserItem>();
    public DbSet<SequflohPartsList> SequflohPartsLists => Set<SequflohPartsList>();
    public DbSet<SequflohPartsListItem> SequflohPartsListItems => Set<SequflohPartsListItem>();
    public DbSet<SequflohPartsListRevision> SequflohPartsListRevisions => Set<SequflohPartsListRevision>();
    public DbSet<SequflohOperationStep> SequflohOperationSteps => Set<SequflohOperationStep>();
    public DbSet<SequflohWorkInstruction> SequflohWorkInstructions => Set<SequflohWorkInstruction>();
    public DbSet<SequflohWorkInstructionTransition> SequflohWorkInstructionTransitions => Set<SequflohWorkInstructionTransition>();
    public DbSet<SequflohInstructionTemplate> SequflohInstructionTemplates => Set<SequflohInstructionTemplate>();
    public DbSet<SequflohTrackingRecord> SequflohTrackingRecords => Set<SequflohTrackingRecord>();
    public DbSet<SequflohTaskEvent> SequflohTaskEvents => Set<SequflohTaskEvent>();
    public DbSet<SequflohTaskAssignee> SequflohTaskAssignees => Set<SequflohTaskAssignee>();
    public DbSet<SequflohTimeEntry> SequflohTimeEntries => Set<SequflohTimeEntry>();
    public DbSet<SequflohTestFile> SequflohTestFiles => Set<SequflohTestFile>();
    public DbSet<SequflohInspectionRequest> SequflohInspectionRequests => Set<SequflohInspectionRequest>();

    private static void ConfigureSequfloh(ModelBuilder b)
    {
        b.Entity<SequflohSettings>(e =>
        {
            e.ToTable("sequfloh_settings");
            foreach (var p in new[] { nameof(Domain.Execution.SequflohSettings.General), nameof(Domain.Execution.SequflohSettings.Numbering), nameof(Domain.Execution.SequflohSettings.WorkInstructions),
                         nameof(Domain.Execution.SequflohSettings.Tracking), nameof(Domain.Execution.SequflohSettings.Validation), nameof(Domain.Execution.SequflohSettings.Fai), nameof(Domain.Execution.SequflohSettings.Repair),
                         nameof(Domain.Execution.SequflohSettings.Display), nameof(Domain.Execution.SequflohSettings.Export), nameof(Domain.Execution.SequflohSettings.Notifications), nameof(Domain.Execution.SequflohSettings.Security) })
                e.Property(p).HasColumnType("jsonb");
        });
        b.Entity<SequflohReferenceValue>(e =>
        {
            e.ToTable("sequfloh_reference_values");
            e.HasIndex(x => new { x.ListKey, x.Code }).IsUnique();
            e.HasIndex(x => new { x.ListKey, x.IsActive, x.SortOrder });
            e.Property(x => x.ListKey).HasMaxLength(60);
            e.Property(x => x.Code).HasMaxLength(60);
            e.Property(x => x.Label).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(1000);
        });
        b.Entity<SequflohUserItem>(e =>
        {
            e.ToTable("sequfloh_user_items");
            e.HasIndex(x => new { x.UserId, x.Kind, x.Path }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Kind, x.LastOpenedAt });
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.ItemType).HasMaxLength(60);
            e.Property(x => x.Path).HasMaxLength(400);
            e.Property(x => x.Label).HasMaxLength(300);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SequflohPartsList>(e =>
        {
            e.ToTable("sequfloh_parts_lists");
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.ParentPartId, x.Status });
            e.HasIndex(x => x.UpdatedAt);
            e.Property(x => x.Number).HasMaxLength(40);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Revision).HasMaxLength(10);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.HasOne<Part>().WithMany().HasForeignKey(x => x.ParentPartId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ReleasedById).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PartsListId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SequflohPartsListItem>(e =>
        {
            e.ToTable("sequfloh_parts_list_items", t => t.HasCheckConstraint("ck_sequfloh_parts_list_items_quantity", "\"Quantity\" > 0"));
            e.HasIndex(x => new { x.PartsListId, x.ItemNumber }).IsUnique();
            e.HasIndex(x => x.PartId);
            e.Property(x => x.PartNumber).HasMaxLength(60);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Unit).HasMaxLength(20);
            e.Property(x => x.AlternatePartNumber).HasMaxLength(60);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<SequflohPartsListRevision>(e =>
        {
            e.ToTable("sequfloh_parts_list_revisions");
            e.HasIndex(x => new { x.PartsListId, x.Revision }).IsUnique();
            e.Property(x => x.Revision).HasMaxLength(10);
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.Snapshot).HasColumnType("jsonb");
            e.HasOne<SequflohPartsList>().WithMany().HasForeignKey(x => x.PartsListId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<SequflohOperationStep>(e =>
        {
            e.ToTable("sequfloh_operation_steps", t => t.HasCheckConstraint("ck_sequfloh_operation_steps_limits", "\"Maximum\" IS NULL OR \"Minimum\" IS NULL OR \"Maximum\" >= \"Minimum\""));
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.IsActive, x.DisplayOrder });
            e.Property(x => x.Number).HasMaxLength(40);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.DetailedAction).HasMaxLength(8000);
            foreach (var p in new[] { nameof(SequflohOperationStep.DefaultTools), nameof(SequflohOperationStep.DefaultParts), nameof(SequflohOperationStep.SafetyWarning), nameof(SequflohOperationStep.QualityCheckpoint), nameof(SequflohOperationStep.ExpectedResult) })
                e.Property(p).HasMaxLength(2000);
            e.Property(x => x.Specification).HasMaxLength(500);
            e.Property(x => x.EvidenceRequirement).HasMaxLength(1000);
            e.Property(x => x.RequiredRole).HasMaxLength(200);
            e.Property(x => x.RequiredQualification).HasMaxLength(200);
            e.Property(x => x.Unit).HasMaxLength(20);
            foreach (var p in new[] { nameof(SequflohOperationStep.Target), nameof(SequflohOperationStep.Minimum), nameof(SequflohOperationStep.Maximum) }) e.Property(p).HasPrecision(18, 6);
        });
        b.Entity<SequflohWorkInstruction>(e =>
        {
            e.ToTable("sequfloh_work_instructions");
            e.HasIndex(x => new { x.Number, x.Revision }).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => new { x.Type, x.Status });
            e.HasIndex(x => new { x.SiteId, x.Status });
            e.HasIndex(x => x.PartId);
            e.HasIndex(x => x.UpdatedAt);
            e.HasOne<Domain.Organization.Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Domain.Organization.Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SequflohPartsList>().WithMany().HasForeignKey(x => x.SourcePartsListId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<SequflohWorkInstruction>().WithMany().HasForeignKey(x => x.PreviousRevisionId).OnDelete(DeleteBehavior.Restrict);
            foreach (var u in new[] { nameof(SequflohWorkInstruction.OwnerId), nameof(SequflohWorkInstruction.AuthorId), nameof(SequflohWorkInstruction.ReleasedById),
                         nameof(SequflohWorkInstruction.AssignedEngineerId) })
                e.HasOne<ApplicationUser>().WithMany().HasForeignKey(u).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Number).HasMaxLength(40);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Revision).HasMaxLength(10);
            foreach (var p in new[] { nameof(SequflohWorkInstruction.PartNumber), nameof(SequflohWorkInstruction.ProductFamily), nameof(SequflohWorkInstruction.Assembly), nameof(SequflohWorkInstruction.Model),
                         nameof(SequflohWorkInstruction.Variant), nameof(SequflohWorkInstruction.Workstation), nameof(SequflohWorkInstruction.ValidationType), nameof(SequflohWorkInstruction.FaiType),
                         nameof(SequflohWorkInstruction.RepairCategory), nameof(SequflohWorkInstruction.RepairType) })
                e.Property(p).HasMaxLength(100);
            foreach (var p in new[] { nameof(SequflohWorkInstruction.PartDescription), nameof(SequflohWorkInstruction.Process), nameof(SequflohWorkInstruction.Operation), nameof(SequflohWorkInstruction.Location),
                         nameof(SequflohWorkInstruction.RepairDescription) })
                e.Property(p).HasMaxLength(300);
            e.Property(x => x.ResultStatus).HasMaxLength(50);
            e.Property(x => x.RevisionReason).HasMaxLength(2000);
            e.Property(x => x.Project).HasMaxLength(100);
            e.Property(x => x.DevelopmentStatus).HasMaxLength(100);
            e.Property(x => x.EngineeringNotes).HasMaxLength(4000);
            e.HasIndex(x => new { x.OwnerId, x.Status });
            e.HasIndex(x => new { x.AssignedEngineerId, x.Status });
            foreach (var p in new[] { nameof(SequflohWorkInstruction.Content), nameof(SequflohWorkInstruction.Steps), nameof(SequflohWorkInstruction.Characteristics), nameof(SequflohWorkInstruction.PartsRows) })
                e.Property(p).HasColumnType("jsonb");
            e.Property(x => x.Blocks).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            e.HasOne<SequflohInstructionTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<SequflohInstructionTemplate>(e =>
        {
            e.ToTable("sequfloh_instruction_templates");
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => new { x.IsActive, x.Type });
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Blocks).HasColumnType("jsonb");
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<SequflohWorkInstructionTransition>(e =>
        {
            e.ToTable("sequfloh_work_instruction_transitions");
            e.HasIndex(x => new { x.WorkInstructionId, x.CreatedAt });
            e.HasOne<SequflohWorkInstruction>().WithMany().HasForeignKey(x => x.WorkInstructionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Action).HasMaxLength(40);
            e.Property(x => x.Revision).HasMaxLength(10);
            e.Property(x => x.Comment).HasMaxLength(4000);
        });
        b.Entity<SequflohTrackingRecord>(e =>
        {
            e.ToTable("sequfloh_tracking_records");
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.Type, x.Status });
            e.HasIndex(x => new { x.Type, x.Result, x.Status });
            e.HasIndex(x => new { x.AssignedUserId, x.Status });
            e.HasIndex(x => new { x.DepartmentId, x.Status });
            e.HasIndex(x => new { x.SiteId, x.Status });
            e.HasIndex(x => new { x.Status, x.DueAt });
            e.HasIndex(x => x.WorkInstructionId);
            e.Property(x => x.TestColumns).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.PartsReplaced).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.ReportOptions).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.Findings).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.CostLines).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            e.HasIndex(x => x.PartId);
            e.HasOne<SequflohWorkInstruction>().WithMany().HasForeignKey(x => x.WorkInstructionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Part>().WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Domain.Organization.Site>().WithMany().HasForeignKey(x => x.SiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Domain.Organization.Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            foreach (var u in new[] { nameof(SequflohTrackingRecord.OwnerId), nameof(SequflohTrackingRecord.AssignedUserId), nameof(SequflohTrackingRecord.ActiveOperatorId), nameof(SequflohTrackingRecord.CompletedById) })
                e.HasOne<ApplicationUser>().WithMany().HasForeignKey(u).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Number).HasMaxLength(40);
            e.Property(x => x.WorkInstructionNumber).HasMaxLength(40);
            e.Property(x => x.WorkInstructionRevision).HasMaxLength(10);
            e.Property(x => x.WorkInstructionTitle).HasMaxLength(300);
            foreach (var p in new[] { nameof(SequflohTrackingRecord.PartNumber), nameof(SequflohTrackingRecord.SerialNumber), nameof(SequflohTrackingRecord.LotNumber), nameof(SequflohTrackingRecord.WorkOrder),
                         nameof(SequflohTrackingRecord.RepairOrder), nameof(SequflohTrackingRecord.CustomerReturnReference), nameof(SequflohTrackingRecord.Project), nameof(SequflohTrackingRecord.Priority),
                         nameof(SequflohTrackingRecord.Result), nameof(SequflohTrackingRecord.FaiType), nameof(SequflohTrackingRecord.FinalDisposition) })
                e.Property(p).HasMaxLength(100);
            foreach (var p in new[] { nameof(SequflohTrackingRecord.PartDescription), nameof(SequflohTrackingRecord.Operation), nameof(SequflohTrackingRecord.RepairDescription), nameof(SequflohTrackingRecord.PauseReason),
                         nameof(SequflohTrackingRecord.HoldReason), nameof(SequflohTrackingRecord.CancelReason), nameof(SequflohTrackingRecord.FailureReason) })
                e.Property(p).HasMaxLength(300);
            foreach (var p in new[] { nameof(SequflohTrackingRecord.VarianceReason), nameof(SequflohTrackingRecord.EngineeringConclusion), nameof(SequflohTrackingRecord.EvidenceReferences), nameof(SequflohTrackingRecord.TechnicianComments),
                         nameof(SequflohTrackingRecord.QualityComments), nameof(SequflohTrackingRecord.EngineeringComments), nameof(SequflohTrackingRecord.Deviations), nameof(SequflohTrackingRecord.NonconformanceReferences),
                         nameof(SequflohTrackingRecord.ReportedCondition), nameof(SequflohTrackingRecord.ObservedCondition) })
                e.Property(p).HasMaxLength(4000);
            foreach (var p in new[] { nameof(SequflohTrackingRecord.MinBenchmarkMinutes), nameof(SequflohTrackingRecord.MaxBenchmarkMinutes), nameof(SequflohTrackingRecord.VarianceMinutes) }) e.Property(p).HasPrecision(12, 2);
            foreach (var p in new[] { nameof(SequflohTrackingRecord.Details), nameof(SequflohTrackingRecord.Steps), nameof(SequflohTrackingRecord.Characteristics), nameof(SequflohTrackingRecord.PartsRows) })
                e.Property(p).HasColumnType("jsonb");
        });
        b.Entity<SequflohTaskAssignee>(e =>
        {
            e.ToTable("sequfloh_task_assignees");
            e.HasKey(x => new { x.TrackingRecordId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.HasOne<SequflohTrackingRecord>().WithMany().HasForeignKey(x => x.TrackingRecordId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<SequflohInspectionRequest>(e =>
        {
            e.ToTable("sequfloh_inspection_requests");
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => new { x.Status, x.RequestedDate });
            e.Property(x => x.Number).HasMaxLength(40);
            e.Property(x => x.PartNumber).HasMaxLength(100);
            e.Property(x => x.PartDescription).HasMaxLength(300);
            e.Property(x => x.Reason).HasMaxLength(100);
            e.Property(x => x.Requirement).HasMaxLength(4000);
            e.Property(x => x.WorkInstructionLabel).HasMaxLength(300);
            e.Property(x => x.Priority).HasMaxLength(50);
            e.Property(x => x.RequestedByName).HasMaxLength(200);
            e.Property(x => x.WorkOrderNumber).HasMaxLength(40);
            e.Property(x => x.Quantity).HasPrecision(18, 4);
        });
        b.Entity<SequflohTestFile>(e =>
        {
            e.ToTable("sequfloh_test_files");
            e.HasIndex(x => x.TrackingRecordId);
            e.Property(x => x.FileName).HasMaxLength(300);
            e.Property(x => x.Batch).HasMaxLength(100);
            e.Property(x => x.KeyValues).HasColumnType("jsonb");
            e.Property(x => x.Kind).HasMaxLength(20).HasDefaultValue("test");
            e.HasOne<SequflohTrackingRecord>().WithMany().HasForeignKey(x => x.TrackingRecordId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<SequflohTimeEntry>(e =>
        {
            e.ToTable("sequfloh_time_entries");
            e.HasIndex(x => new { x.UserId, x.StartedAt });
            // one open clock-on per person per work order
            e.HasIndex(x => new { x.TrackingRecordId, x.UserId }, "IX_sequfloh_time_entries_running").HasFilter("\"StoppedAt\" IS NULL").IsUnique();
            e.HasIndex(x => x.TrackingRecordId);
            e.HasOne<SequflohTrackingRecord>().WithMany().HasForeignKey(x => x.TrackingRecordId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.TrackingNumber).HasMaxLength(40);
            e.Property(x => x.Events).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.UserName).HasMaxLength(200);
        });
        b.Entity<SequflohTaskEvent>(e =>
        {
            e.ToTable("sequfloh_task_events");
            e.HasIndex(x => x.OperationId).IsUnique();
            e.HasIndex(x => new { x.TrackingRecordId, x.CreatedAt });
            e.HasIndex(x => new { x.EventType, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne<SequflohTrackingRecord>().WithMany().HasForeignKey(x => x.TrackingRecordId).OnDelete(DeleteBehavior.Restrict);
            foreach (var u in new[] { nameof(SequflohTaskEvent.UserId), nameof(SequflohTaskEvent.AssignedBeforeId), nameof(SequflohTaskEvent.AssignedAfterId) })
                e.HasOne<ApplicationUser>().WithMany().HasForeignKey(u).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.TrackingNumber).HasMaxLength(40);
            e.Property(x => x.EventType).HasMaxLength(40);
            e.Property(x => x.UserName).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(300);
            e.Property(x => x.Comment).HasMaxLength(4000);
        });
        b.Entity<Part>(e =>
        {
            e.Property(x => x.ProductFamily).HasMaxLength(100);
            e.Property(x => x.AssemblyType).HasMaxLength(100);
            e.Property(x => x.DrawingNumber).HasMaxLength(100);
            e.Property(x => x.Specification).HasMaxLength(200);
            e.HasIndex(x => new { x.Status, x.PartNumber });
        });
    }
}
