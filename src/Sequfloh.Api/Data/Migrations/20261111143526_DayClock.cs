using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class DayClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DayClock",
                schema: "sqf",
                table: "sequfloh_settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "sequfloh_day_clocks",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    WorkingLate = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_day_clocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_day_clocks_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "sqf",
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sequfloh_day_clocks_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sequfloh_day_segments",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DayClockId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    TrackingNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TimeEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    IndirectValueId = table.Column<Guid>(type: "uuid", nullable: true),
                    TaskName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Seconds = table.Column<long>(type: "bigint", nullable: false),
                    ResumeKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ResumeTrackingRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResumeTrackingNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ResumeTimeEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResumeIndirectValueId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResumeTaskName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResumeNote = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    VoidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VoidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OriginalStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OriginalEndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AdjustedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AdjustedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AdjustReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_day_segments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_day_segments_sequfloh_day_clocks_DayClockId",
                        column: x => x.DayClockId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_day_clocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sequfloh_day_segments_sequfloh_reference_values_IndirectVal~",
                        column: x => x.IndirectValueId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_reference_values",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_day_segments_sequfloh_time_entries_TimeEntryId",
                        column: x => x.TimeEntryId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_time_entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sequfloh_day_segments_sequfloh_tracking_records_TrackingRec~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sequfloh_day_segments_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_day_clocks_DepartmentId",
                schema: "sqf",
                table: "sequfloh_day_clocks",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_day_clocks_open",
                schema: "sqf",
                table: "sequfloh_day_clocks",
                column: "UserId",
                unique: true,
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_day_clocks_UserId_StartedAt",
                schema: "sqf",
                table: "sequfloh_day_clocks",
                columns: new[] { "UserId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_day_segments_DayClockId_StartedAt",
                schema: "sqf",
                table: "sequfloh_day_segments",
                columns: new[] { "DayClockId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_day_segments_IndirectValueId",
                schema: "sqf",
                table: "sequfloh_day_segments",
                column: "IndirectValueId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_day_segments_open",
                schema: "sqf",
                table: "sequfloh_day_segments",
                column: "UserId",
                unique: true,
                filter: "\"EndedAt\" IS NULL AND \"VoidedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_day_segments_TimeEntryId",
                schema: "sqf",
                table: "sequfloh_day_segments",
                column: "TimeEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_day_segments_TrackingRecordId",
                schema: "sqf",
                table: "sequfloh_day_segments",
                column: "TrackingRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_day_segments_UserId_StartedAt",
                schema: "sqf",
                table: "sequfloh_day_segments",
                columns: new[] { "UserId", "StartedAt" });

            // the built-in Administrator role holds every permission, including the day clock
            migrationBuilder.Sql("""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", p FROM sqf.roles, unnest(ARRAY[
                    'sequfloh.view_own_day_clock',
                    'sequfloh.view_team_day_clock',
                    'sequfloh.view_all_day_clock',
                    'sequfloh.correct_day_clock',
                    'sequfloh.manage_day_clock']) AS p
                WHERE "IsSystem"
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM sqf.role_permissions WHERE "Permission" IN (
                    'sequfloh.view_own_day_clock',
                    'sequfloh.view_team_day_clock',
                    'sequfloh.view_all_day_clock',
                    'sequfloh.correct_day_clock',
                    'sequfloh.manage_day_clock');
                """);

            migrationBuilder.DropTable(
                name: "sequfloh_day_segments",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "sequfloh_day_clocks",
                schema: "sqf");

            migrationBuilder.DropColumn(
                name: "DayClock",
                schema: "sqf",
                table: "sequfloh_settings");
        }
    }
}
