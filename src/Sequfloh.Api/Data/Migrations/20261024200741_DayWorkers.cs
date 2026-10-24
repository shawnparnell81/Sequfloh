using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class DayWorkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkerId",
                schema: "sqf",
                table: "sequfloh_crew_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkerName",
                schema: "sqf",
                table: "sequfloh_crew_sessions",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sequfloh_day_workers",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackingNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_day_workers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_day_workers_sequfloh_tracking_records_TrackingReco~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_crew_sessions_WorkerId",
                schema: "sqf",
                table: "sequfloh_crew_sessions",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_day_workers_TrackingRecordId",
                schema: "sqf",
                table: "sequfloh_day_workers",
                column: "TrackingRecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sequfloh_day_workers",
                schema: "sqf");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_crew_sessions_WorkerId",
                schema: "sqf",
                table: "sequfloh_crew_sessions");

            migrationBuilder.DropColumn(
                name: "WorkerId",
                schema: "sqf",
                table: "sequfloh_crew_sessions");

            migrationBuilder.DropColumn(
                name: "WorkerName",
                schema: "sqf",
                table: "sequfloh_crew_sessions");
        }
    }
}
