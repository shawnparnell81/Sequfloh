using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "sqf");

            migrationBuilder.CreateTable(
                name: "attachments",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    IsLocked = table.Column<bool>(type: "boolean", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audit_entries",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Changes = table.Column<string>(type: "jsonb", nullable: true),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "number_sequences",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Prefix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Padding = table.Column<int>(type: "integer", nullable: false),
                    ResetYearly = table.Column<bool>(type: "boolean", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    NextValue = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_number_sequences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "organization_settings",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OrganizationName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_operation_steps",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DetailedAction = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    DefaultTools = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DefaultParts = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Specification = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Target = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Minimum = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Maximum = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    SafetyWarning = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    QualityCheckpoint = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ExpectedResult = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EvidenceRequirement = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequiredRole = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequiredQualification = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_operation_steps", x => x.Id);
                    table.CheckConstraint("ck_sequfloh_operation_steps_limits", "\"Maximum\" IS NULL OR \"Minimum\" IS NULL OR \"Maximum\" >= \"Minimum\"");
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_reference_values",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsSeeded = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_reference_values", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_settings",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    General = table.Column<string>(type: "jsonb", nullable: false),
                    Numbering = table.Column<string>(type: "jsonb", nullable: false),
                    WorkInstructions = table.Column<string>(type: "jsonb", nullable: false),
                    Tracking = table.Column<string>(type: "jsonb", nullable: false),
                    Validation = table.Column<string>(type: "jsonb", nullable: false),
                    Fai = table.Column<string>(type: "jsonb", nullable: false),
                    Repair = table.Column<string>(type: "jsonb", nullable: false),
                    Display = table.Column<string>(type: "jsonb", nullable: false),
                    Export = table.Column<string>(type: "jsonb", nullable: false),
                    Notifications = table.Column<string>(type: "jsonb", nullable: false),
                    Security = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "text", nullable: true),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    JobTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultSiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSignInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MustChangePassword = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedSignIns = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TokenVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "sqf",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permission = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.RoleId, x.Permission });
                    table.ForeignKey(
                        name: "FK_role_permissions_roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "sqf",
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_departments_sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "sqf",
                        principalTable: "sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RecordType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Link = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notifications_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_instruction_templates",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: true),
                    Blocks = table.Column<string>(type: "jsonb", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_instruction_templates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_instruction_templates_users_OwnerId",
                        column: x => x.OwnerId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_user_items",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ItemType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Path = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    LastOpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_user_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_user_items_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "parts",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PartType = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    UnitOfMeasure = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    Material = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    HasSpecialCharacteristics = table.Column<bool>(type: "boolean", nullable: false),
                    ProductFamily = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AssemblyType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsRepairable = table.Column<bool>(type: "boolean", nullable: false),
                    IsSerialized = table.Column<bool>(type: "boolean", nullable: false),
                    IsLotControlled = table.Column<bool>(type: "boolean", nullable: false),
                    DrawingNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Specification = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_parts_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "sqf",
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_parts_sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "sqf",
                        principalTable: "sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_role_assignments",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_role_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_role_assignments_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "sqf",
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_role_assignments_roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "sqf",
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_role_assignments_sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "sqf",
                        principalTable: "sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_role_assignments_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "part_revisions",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_part_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_part_revisions_parts_PartId",
                        column: x => x.PartId,
                        principalSchema: "sqf",
                        principalTable: "parts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_parts_lists",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ParentPartId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ListType = table.Column<int>(type: "integer", nullable: false),
                    ApprovedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_parts_lists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_parts_lists_parts_ParentPartId",
                        column: x => x.ParentPartId,
                        principalSchema: "sqf",
                        principalTable: "parts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_parts_lists_users_ApprovedById",
                        column: x => x.ApprovedById,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_parts_list_items",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartsListId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemNumber = table.Column<int>(type: "integer", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    AlternatePartNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Traceability = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_parts_list_items", x => x.Id);
                    table.CheckConstraint("ck_sequfloh_parts_list_items_quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_sequfloh_parts_list_items_parts_PartId",
                        column: x => x.PartId,
                        principalSchema: "sqf",
                        principalTable: "parts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_parts_list_items_sequfloh_parts_lists_PartsListId",
                        column: x => x.PartsListId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_parts_lists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_parts_list_revisions",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartsListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    StatusAtRevision = table.Column<int>(type: "integer", nullable: false),
                    Snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_parts_list_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_parts_list_revisions_sequfloh_parts_lists_PartsLis~",
                        column: x => x.PartsListId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_parts_lists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_work_instructions",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Revision = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StatusBeforeArchive = table.Column<int>(type: "integer", nullable: true),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApproverId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PartDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ProductFamily = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Assembly = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Variant = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Process = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Operation = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Workstation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Location = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ValidationType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FaiType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RepairCategory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RepairType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RepairDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SourcePartsListId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultStatus = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ObsoleteAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedById = table.Column<Guid>(type: "uuid", nullable: true),
                    RevisionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AssignedEngineerId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetCompletionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Project = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    EngineeringNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DevelopmentStatus = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReleaseRecommendedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseRecommendedById = table.Column<Guid>(type: "uuid", nullable: true),
                    Content = table.Column<string>(type: "jsonb", nullable: false),
                    Steps = table.Column<string>(type: "jsonb", nullable: false),
                    Characteristics = table.Column<string>(type: "jsonb", nullable: false),
                    PartsRows = table.Column<string>(type: "jsonb", nullable: false),
                    Blocks = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_work_instructions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "sqf",
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_parts_PartId",
                        column: x => x.PartId,
                        principalSchema: "sqf",
                        principalTable: "parts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_sequfloh_instruction_templates_T~",
                        column: x => x.TemplateId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_instruction_templates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_sequfloh_parts_lists_SourceParts~",
                        column: x => x.SourcePartsListId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_parts_lists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_sequfloh_work_instructions_Previ~",
                        column: x => x.PreviousRevisionId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_work_instructions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "sqf",
                        principalTable: "sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_users_ApprovedById",
                        column: x => x.ApprovedById,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_users_ApproverId",
                        column: x => x.ApproverId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_users_AssignedEngineerId",
                        column: x => x.AssignedEngineerId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_users_AuthorId",
                        column: x => x.AuthorId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_users_OwnerId",
                        column: x => x.OwnerId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_users_ReleaseRecommendedById",
                        column: x => x.ReleaseRecommendedById,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_users_ReleasedById",
                        column: x => x.ReleasedById,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_users_ReviewedById",
                        column: x => x.ReviewedById,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instructions_users_ReviewerId",
                        column: x => x.ReviewerId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_tracking_records",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    WorkInstructionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkInstructionNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    WorkInstructionRevision = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    WorkInstructionTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PartDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SerialNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LotNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WorkOrder = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RepairOrder = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CustomerReturnReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Project = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Operation = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PlannedStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActualStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActualCompletion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentStateStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActiveSeconds = table.Column<long>(type: "bigint", nullable: false),
                    PausedSeconds = table.Column<long>(type: "bigint", nullable: false),
                    HoldSeconds = table.Column<long>(type: "bigint", nullable: false),
                    ActiveOperatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    StateVersion = table.Column<int>(type: "integer", nullable: false),
                    LastTransitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastTransitionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MinBenchmarkMinutes = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    MaxBenchmarkMinutes = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    VarianceMinutes = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    VarianceReason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Result = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApprovalStatus = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    QualityReviewRequired = table.Column<bool>(type: "boolean", nullable: false),
                    EngineeringReviewRequired = table.Column<bool>(type: "boolean", nullable: false),
                    QualityReviewedById = table.Column<Guid>(type: "uuid", nullable: true),
                    QualityReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EngineeringReviewedById = table.Column<Guid>(type: "uuid", nullable: true),
                    EngineeringReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EngineeringConclusion = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    EvidenceReferences = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    TechnicianComments = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    QualityComments = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    EngineeringComments = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Deviations = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    NonconformanceReferences = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PauseReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    HoldReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ReportedCondition = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ObservedCondition = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RepairDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    FinalDisposition = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FaiType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Details = table.Column<string>(type: "jsonb", nullable: false),
                    Steps = table.Column<string>(type: "jsonb", nullable: false),
                    Characteristics = table.Column<string>(type: "jsonb", nullable: false),
                    PartsRows = table.Column<string>(type: "jsonb", nullable: false),
                    Signoffs = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_tracking_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_tracking_records_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "sqf",
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_tracking_records_parts_PartId",
                        column: x => x.PartId,
                        principalSchema: "sqf",
                        principalTable: "parts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_tracking_records_sequfloh_work_instructions_WorkIn~",
                        column: x => x.WorkInstructionId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_work_instructions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_tracking_records_sites_SiteId",
                        column: x => x.SiteId,
                        principalSchema: "sqf",
                        principalTable: "sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_tracking_records_users_ActiveOperatorId",
                        column: x => x.ActiveOperatorId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_tracking_records_users_AssignedUserId",
                        column: x => x.AssignedUserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_tracking_records_users_CompletedById",
                        column: x => x.CompletedById,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_tracking_records_users_EngineeringReviewedById",
                        column: x => x.EngineeringReviewedById,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_tracking_records_users_OwnerId",
                        column: x => x.OwnerId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_tracking_records_users_QualityReviewedById",
                        column: x => x.QualityReviewedById,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_work_instruction_transitions",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkInstructionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FromStatus = table.Column<int>(type: "integer", nullable: true),
                    ToStatus = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Revision = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_work_instruction_transitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instruction_transitions_sequfloh_work_instruc~",
                        column: x => x.WorkInstructionId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_work_instructions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_work_instruction_transitions_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_task_events",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EventType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FromStatus = table.Column<int>(type: "integer", nullable: true),
                    ToStatus = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AssignedBeforeId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedAfterId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ActiveSeconds = table.Column<long>(type: "bigint", nullable: false),
                    PausedSeconds = table.Column<long>(type: "bigint", nullable: false),
                    HoldSeconds = table.Column<long>(type: "bigint", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_task_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_task_events_sequfloh_tracking_records_TrackingReco~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_task_events_users_AssignedAfterId",
                        column: x => x.AssignedAfterId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_task_events_users_AssignedBeforeId",
                        column: x => x.AssignedBeforeId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_task_events_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attachments_OwnerType_OwnerId",
                schema: "sqf",
                table: "attachments",
                columns: new[] { "OwnerType", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_EntityType_EntityId",
                schema: "sqf",
                table: "audit_entries",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_OccurredAt",
                schema: "sqf",
                table: "audit_entries",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_audit_entries_UserId",
                schema: "sqf",
                table: "audit_entries",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_departments_SiteId_Code",
                schema: "sqf",
                table: "departments",
                columns: new[] { "SiteId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_UserId_ReadAt",
                schema: "sqf",
                table: "notifications",
                columns: new[] { "UserId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_number_sequences_RecordType_SiteId",
                schema: "sqf",
                table: "number_sequences",
                columns: new[] { "RecordType", "SiteId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_part_revisions_PartId_Revision",
                schema: "sqf",
                table: "part_revisions",
                columns: new[] { "PartId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_parts_DepartmentId",
                schema: "sqf",
                table: "parts",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_parts_PartNumber",
                schema: "sqf",
                table: "parts",
                column: "PartNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_parts_SiteId",
                schema: "sqf",
                table: "parts",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_parts_Status_PartNumber",
                schema: "sqf",
                table: "parts",
                columns: new[] { "Status", "PartNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_roles_Name",
                schema: "sqf",
                table: "roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_instruction_templates_IsActive_Type",
                schema: "sqf",
                table: "sequfloh_instruction_templates",
                columns: new[] { "IsActive", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_instruction_templates_Name",
                schema: "sqf",
                table: "sequfloh_instruction_templates",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_instruction_templates_OwnerId",
                schema: "sqf",
                table: "sequfloh_instruction_templates",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_operation_steps_IsActive_DisplayOrder",
                schema: "sqf",
                table: "sequfloh_operation_steps",
                columns: new[] { "IsActive", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_operation_steps_Number",
                schema: "sqf",
                table: "sequfloh_operation_steps",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_parts_list_items_PartId",
                schema: "sqf",
                table: "sequfloh_parts_list_items",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_parts_list_items_PartsListId_ItemNumber",
                schema: "sqf",
                table: "sequfloh_parts_list_items",
                columns: new[] { "PartsListId", "ItemNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_parts_list_revisions_PartsListId_Revision",
                schema: "sqf",
                table: "sequfloh_parts_list_revisions",
                columns: new[] { "PartsListId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_parts_lists_ApprovedById",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                column: "ApprovedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_parts_lists_Number",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_parts_lists_ParentPartId_Status",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                columns: new[] { "ParentPartId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_parts_lists_UpdatedAt",
                schema: "sqf",
                table: "sequfloh_parts_lists",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_reference_values_ListKey_Code",
                schema: "sqf",
                table: "sequfloh_reference_values",
                columns: new[] { "ListKey", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_reference_values_ListKey_IsActive_SortOrder",
                schema: "sqf",
                table: "sequfloh_reference_values",
                columns: new[] { "ListKey", "IsActive", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_task_events_AssignedAfterId",
                schema: "sqf",
                table: "sequfloh_task_events",
                column: "AssignedAfterId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_task_events_AssignedBeforeId",
                schema: "sqf",
                table: "sequfloh_task_events",
                column: "AssignedBeforeId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_task_events_EventType_CreatedAt",
                schema: "sqf",
                table: "sequfloh_task_events",
                columns: new[] { "EventType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_task_events_OperationId",
                schema: "sqf",
                table: "sequfloh_task_events",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_task_events_TrackingRecordId_CreatedAt",
                schema: "sqf",
                table: "sequfloh_task_events",
                columns: new[] { "TrackingRecordId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_task_events_UserId_CreatedAt",
                schema: "sqf",
                table: "sequfloh_task_events",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_ActiveOperatorId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "ActiveOperatorId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_ApprovalStatus_OwnerId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                columns: new[] { "ApprovalStatus", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_AssignedUserId_Status",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                columns: new[] { "AssignedUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_CompletedById",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "CompletedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_DepartmentId_Status",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                columns: new[] { "DepartmentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_EngineeringReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "EngineeringReviewedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_Number",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_OwnerId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_PartId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_QualityReviewedById",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "QualityReviewedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_SiteId_Status",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                columns: new[] { "SiteId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_Status_DueAt",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_Type_Result_Status",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                columns: new[] { "Type", "Result", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_Type_Status",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                columns: new[] { "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_WorkInstructionId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "WorkInstructionId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_user_items_UserId_Kind_LastOpenedAt",
                schema: "sqf",
                table: "sequfloh_user_items",
                columns: new[] { "UserId", "Kind", "LastOpenedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_user_items_UserId_Kind_Path",
                schema: "sqf",
                table: "sequfloh_user_items",
                columns: new[] { "UserId", "Kind", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instruction_transitions_UserId",
                schema: "sqf",
                table: "sequfloh_work_instruction_transitions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instruction_transitions_WorkInstructionId_Cre~",
                schema: "sqf",
                table: "sequfloh_work_instruction_transitions",
                columns: new[] { "WorkInstructionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_ApprovedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ApprovedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_ApproverId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ApproverId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_AssignedEngineerId_Status",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                columns: new[] { "AssignedEngineerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_AuthorId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_DepartmentId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_FamilyId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_Number_Revision",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                columns: new[] { "Number", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_OwnerId_Status",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                columns: new[] { "OwnerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_PartId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_PreviousRevisionId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "PreviousRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_ReleasedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ReleasedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_ReleaseRecommendedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ReleaseRecommendedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_ReviewedById",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ReviewedById");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_ReviewerId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "ReviewerId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_SiteId_Status",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                columns: new[] { "SiteId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_SourcePartsListId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "SourcePartsListId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_Status_ReviewerId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                columns: new[] { "Status", "ReviewerId" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_TemplateId",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_Type_Status",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                columns: new[] { "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_work_instructions_UpdatedAt",
                schema: "sqf",
                table: "sequfloh_work_instructions",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_sites_Code",
                schema: "sqf",
                table: "sites",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_role_assignments_DepartmentId",
                schema: "sqf",
                table: "user_role_assignments",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_user_role_assignments_RoleId",
                schema: "sqf",
                table: "user_role_assignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_user_role_assignments_SiteId",
                schema: "sqf",
                table: "user_role_assignments",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_user_role_assignments_UserId_RoleId_SiteId_DepartmentId",
                schema: "sqf",
                table: "user_role_assignments",
                columns: new[] { "UserId", "RoleId", "SiteId", "DepartmentId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_users_NormalizedEmail",
                schema: "sqf",
                table: "users",
                column: "NormalizedEmail",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachments",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "audit_entries",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "number_sequences",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "organization_settings",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "part_revisions",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_operation_steps",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_parts_list_items",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_parts_list_revisions",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_reference_values",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_settings",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_task_events",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_user_items",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_work_instruction_transitions",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "user_role_assignments",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_tracking_records",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_work_instructions",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_instruction_templates",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_parts_lists",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "parts",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "users",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "departments",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sites",
                schema: "sqf");
        }
    }
}
