using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class Projects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                schema: "sqf",
                table: "sequfloh_inspection_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "sequfloh_projects",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_projects", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_tracking_records_ProjectId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_inspection_requests_ProjectId",
                schema: "sqf",
                table: "sequfloh_inspection_requests",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_projects_Number",
                schema: "sqf",
                table: "sequfloh_projects",
                column: "Number",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_sequfloh_inspection_requests_sequfloh_projects_ProjectId",
                schema: "sqf",
                table: "sequfloh_inspection_requests",
                column: "ProjectId",
                principalSchema: "sqf",
                principalTable: "sequfloh_projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sequfloh_tracking_records_sequfloh_projects_ProjectId",
                schema: "sqf",
                table: "sequfloh_tracking_records",
                column: "ProjectId",
                principalSchema: "sqf",
                principalTable: "sequfloh_projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sequfloh_inspection_requests_sequfloh_projects_ProjectId",
                schema: "sqf",
                table: "sequfloh_inspection_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_sequfloh_tracking_records_sequfloh_projects_ProjectId",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropTable(
                name: "sequfloh_projects",
                schema: "sqf");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_tracking_records_ProjectId",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropIndex(
                name: "IX_sequfloh_inspection_requests_ProjectId",
                schema: "sqf",
                table: "sequfloh_inspection_requests");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                schema: "sqf",
                table: "sequfloh_tracking_records");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                schema: "sqf",
                table: "sequfloh_inspection_requests");
        }
    }
}
