using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class InspectionRequestAssignee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedUserId",
                schema: "sqf",
                table: "sequfloh_inspection_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedUserName",
                schema: "sqf",
                table: "sequfloh_inspection_requests",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedUserId",
                schema: "sqf",
                table: "sequfloh_inspection_requests");

            migrationBuilder.DropColumn(
                name: "AssignedUserName",
                schema: "sqf",
                table: "sequfloh_inspection_requests");
        }
    }
}
