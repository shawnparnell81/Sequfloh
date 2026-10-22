using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImportedFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "imported_files",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    SourceFormat = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    OriginalStorageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentVersion = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "jsonb", nullable: false),
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
                    table.PrimaryKey("PK_imported_files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "imported_file_versions",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportedFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_imported_file_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_imported_file_versions_imported_files_ImportedFileId",
                        column: x => x.ImportedFileId,
                        principalSchema: "sqf",
                        principalTable: "imported_files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_imported_file_versions_ImportedFileId_Version",
                schema: "sqf",
                table: "imported_file_versions",
                columns: new[] { "ImportedFileId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_imported_files_Number",
                schema: "sqf",
                table: "imported_files",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_imported_files_SiteId_DepartmentId",
                schema: "sqf",
                table: "imported_files",
                columns: new[] { "SiteId", "DepartmentId" });

            // New permissions for imported files, granted from what roles already hold: the built-in administrator role gets both, roles that
            // can open the Quality or Engineering folders can view imported files, and roles that can edit those folders can import and edit them.
            migrationBuilder.Sql("""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", p FROM sqf.roles, (VALUES ('sequfloh.view_imported_files'), ('sequfloh.import_files')) AS v(p) WHERE "IsSystem"
                ON CONFLICT DO NOTHING;
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT DISTINCT "RoleId", 'sequfloh.view_imported_files' FROM sqf.role_permissions
                 WHERE "Permission" IN ('sequfloh.view_quality_folder', 'sequfloh.view_engineering_folder', 'sequfloh.edit_quality_folder', 'sequfloh.edit_engineering_folder')
                ON CONFLICT DO NOTHING;
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT DISTINCT "RoleId", 'sequfloh.import_files' FROM sqf.role_permissions
                 WHERE "Permission" IN ('sequfloh.edit_quality_folder', 'sequfloh.edit_engineering_folder')
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM sqf.role_permissions WHERE "Permission" IN ('sequfloh.view_imported_files', 'sequfloh.import_files');""");
            migrationBuilder.DropTable(
                name: "imported_file_versions",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "imported_files",
                schema: "sqf");
        }
    }
}
