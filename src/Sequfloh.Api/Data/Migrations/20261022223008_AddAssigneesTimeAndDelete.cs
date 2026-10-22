using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAssigneesTimeAndDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sequfloh_task_assignees",
                schema: "sqf",
                columns: table => new
                {
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_task_assignees", x => new { x.TrackingRecordId, x.UserId });
                    table.ForeignKey(
                        name: "FK_sequfloh_task_assignees_sequfloh_tracking_records_TrackingR~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sequfloh_task_assignees_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_time_entries",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StoppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Seconds = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_time_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_time_entries_sequfloh_tracking_records_TrackingRec~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_time_entries_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_task_assignees_UserId",
                schema: "sqf",
                table: "sequfloh_task_assignees",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_time_entries_running",
                schema: "sqf",
                table: "sequfloh_time_entries",
                columns: new[] { "TrackingRecordId", "UserId" },
                unique: true,
                filter: "\"StoppedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_time_entries_TrackingRecordId",
                schema: "sqf",
                table: "sequfloh_time_entries",
                column: "TrackingRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_time_entries_UserId_StartedAt",
                schema: "sqf",
                table: "sequfloh_time_entries",
                columns: new[] { "UserId", "StartedAt" });

            // everyone already assigned to a work order becomes its first assignee; the built-in administrator role can delete records
            migrationBuilder.Sql("""
                INSERT INTO sqf.sequfloh_task_assignees ("TrackingRecordId", "UserId", "AddedAt")
                SELECT "Id", "AssignedUserId", COALESCE("LastTransitionAt", "CreatedAt") FROM sqf.sequfloh_tracking_records WHERE "AssignedUserId" IS NOT NULL
                ON CONFLICT DO NOTHING;
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", 'sequfloh.delete_records' FROM sqf.roles WHERE "IsSystem"
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sequfloh_task_assignees",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_time_entries",
                schema: "sqf");
        }
    }
}
