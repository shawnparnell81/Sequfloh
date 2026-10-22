using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDrawings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "drawing_folders",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    PartId = table.Column<Guid>(type: "uuid", nullable: true),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("PK_drawing_folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_drawing_folders_drawing_folders_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "sqf",
                        principalTable: "drawing_folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_drawing_folders_parts_PartId",
                        column: x => x.PartId,
                        principalSchema: "sqf",
                        principalTable: "parts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "drawings",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    DrawingNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CurrentRevision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("PK_drawings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_drawings_drawing_folders_FolderId",
                        column: x => x.FolderId,
                        principalSchema: "sqf",
                        principalTable: "drawing_folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "drawing_revisions",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DrawingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drawing_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_drawing_revisions_drawings_DrawingId",
                        column: x => x.DrawingId,
                        principalSchema: "sqf",
                        principalTable: "drawings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_drawing_folders_ParentId",
                schema: "sqf",
                table: "drawing_folders",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_drawing_folders_PartId",
                schema: "sqf",
                table: "drawing_folders",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_drawing_folders_SiteId_DepartmentId",
                schema: "sqf",
                table: "drawing_folders",
                columns: new[] { "SiteId", "DepartmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_drawing_revisions_DrawingId_Revision",
                schema: "sqf",
                table: "drawing_revisions",
                columns: new[] { "DrawingId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_drawings_DrawingNumber",
                schema: "sqf",
                table: "drawings",
                column: "DrawingNumber");

            migrationBuilder.CreateIndex(
                name: "IX_drawings_FolderId",
                schema: "sqf",
                table: "drawings",
                column: "FolderId");

            // New drawing permissions, granted from what roles already hold: the built-in administrator role gets both, roles that can open the
            // Engineering folder can view drawings, and roles that can edit it can manage drawings.
            migrationBuilder.Sql("""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", p FROM sqf.roles, (VALUES ('sequfloh.view_drawings'), ('sequfloh.manage_drawings')) AS v(p) WHERE "IsSystem"
                ON CONFLICT DO NOTHING;
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT DISTINCT "RoleId", 'sequfloh.view_drawings' FROM sqf.role_permissions
                 WHERE "Permission" IN ('sequfloh.view_engineering_folder', 'sequfloh.edit_engineering_folder')
                ON CONFLICT DO NOTHING;
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT DISTINCT "RoleId", 'sequfloh.manage_drawings' FROM sqf.role_permissions
                 WHERE "Permission" = 'sequfloh.edit_engineering_folder'
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM sqf.role_permissions WHERE "Permission" IN ('sequfloh.view_drawings', 'sequfloh.manage_drawings');""");
            migrationBuilder.DropTable(
                name: "drawing_revisions",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "drawings",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "drawing_folders",
                schema: "sqf");
        }
    }
}
