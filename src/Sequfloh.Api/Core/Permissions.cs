namespace Sequfloh.Domain.Security;

/// <summary>
/// Permission catalog. Roles reference these codes as data; nothing in the code checks a role name.
/// </summary>
public static class Permissions
{
    public const string AdminUsers = "admin.users";
    public const string AdminRoles = "admin.roles";
    public const string AdminOrganization = "admin.organization";
    public const string AuditTrailView = "audittrail.view";
    public const string MasterDataView = "parts.view";
    public const string MasterDataEdit = "parts.edit";

    public const string SequflohAccess = "sequfloh.access";
    public const string SequflohViewDashboard = "sequfloh.view_dashboard";
    public const string SequflohViewLiveExecution = "sequfloh.view_live_execution";
    public const string SequflohDispatchTask = "sequfloh.dispatch_task";
    public const string SequflohStartTask = "sequfloh.start_task";
    public const string SequflohPauseTask = "sequfloh.pause_task";
    public const string SequflohResumeTask = "sequfloh.resume_task";
    public const string SequflohHoldTask = "sequfloh.hold_task";
    public const string SequflohCompleteTask = "sequfloh.complete_task";
    public const string SequflohFailTask = "sequfloh.fail_task";
    public const string SequflohReassignTask = "sequfloh.reassign_task";
    public const string SequflohCancelTask = "sequfloh.cancel_task";
    public const string SequflohCreateWorkInstruction = "sequfloh.create_work_instruction";
    public const string SequflohEditDraftWorkInstruction = "sequfloh.edit_draft_work_instruction";
    public const string SequflohReleaseWorkInstruction = "sequfloh.release_work_instruction";
    public const string SequflohReviseWorkInstruction = "sequfloh.revise_work_instruction";
    public const string SequflohObsoleteWorkInstruction = "sequfloh.obsolete_work_instruction";
    public const string SequflohCreateValidationRecord = "sequfloh.create_validation_record";
    public const string SequflohCreateFaiRecord = "sequfloh.create_fai_record";
    public const string SequflohCreateRepairRecord = "sequfloh.create_repair_record";
    public const string SequflohCreateProductionTask = "sequfloh.create_production_task";
    public const string SequflohEditParts = "sequfloh.edit_parts";
    public const string SequflohManagePartsLists = "sequfloh.manage_parts_lists";
    public const string SequflohManageOperationSteps = "sequfloh.manage_operation_steps";
    public const string SequflohViewQualityFolder = "sequfloh.view_quality_folder";
    public const string SequflohEditQualityFolder = "sequfloh.edit_quality_folder";
    public const string SequflohViewEngineeringFolder = "sequfloh.view_engineering_folder";
    public const string SequflohEditEngineeringFolder = "sequfloh.edit_engineering_folder";
    public const string SequflohViewImportedFiles = "sequfloh.view_imported_files";
    public const string SequflohImportFiles = "sequfloh.import_files";
    public const string SequflohViewDrawings = "sequfloh.view_drawings";
    public const string SequflohManageDrawings = "sequfloh.manage_drawings";
    public const string SequflohViewReports = "sequfloh.view_reports";
    public const string SequflohExportRecords = "sequfloh.export_records";
    public const string SequflohViewAuditTrail = "sequfloh.view_audit_trail";
    public const string SequflohManageSettings = "sequfloh.manage_settings";
    public const string SequflohManageDropdowns = "sequfloh.manage_dropdowns";
    public const string SequflohManageModuleAccess = "sequfloh.manage_module_access";
    public const string SequflohDeleteRecords = "sequfloh.delete_records";
    public const string SequflohManageCrewTime = "sequfloh.manage_crew_time";
    public const string SequflohManageChargebacks = "sequfloh.manage_chargebacks";
    public const string SequflohManageProjects = "sequfloh.manage_projects";
    public const string SequflohReopenCompleted = "sequfloh.reopen_completed";

    /// <summary>The delete permission that goes with a module's edit permission, or null when there is none.</summary>
    public static string? DeleteFor(string editPermission) =>
        editPermission.StartsWith("sequfloh.", StringComparison.Ordinal) ? SequflohDeleteRecords : null;

    public static readonly IReadOnlyList<string> All = typeof(Permissions)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();

    /// <summary>Permissions that only make sense organization-wide (never site-scoped).</summary>
    public static readonly IReadOnlySet<string> GlobalOnly = new HashSet<string> { AdminUsers, AdminRoles, AdminOrganization };
}
