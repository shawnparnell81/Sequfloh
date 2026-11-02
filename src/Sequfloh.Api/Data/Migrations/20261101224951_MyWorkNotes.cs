using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class MyWorkNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sequfloh_user_notes",
                schema: "sqf",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsDone = table.Column<bool>(type: "boolean", nullable: false),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemindOn = table.Column<DateOnly>(type: "date", nullable: true),
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Pinned = table.Column<bool>(type: "boolean", nullable: false),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sequfloh_user_notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sequfloh_user_notes_sequfloh_tracking_records_TrackingRecor~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sequfloh_user_notes_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "sqf",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_user_notes_TrackingRecordId",
                schema: "sqf",
                table: "sequfloh_user_notes",
                column: "TrackingRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_sequfloh_user_notes_UserId_IsDone",
                schema: "sqf",
                table: "sequfloh_user_notes",
                columns: new[] { "UserId", "IsDone" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sequfloh_user_notes",
                schema: "sqf");
        }
    }
}
