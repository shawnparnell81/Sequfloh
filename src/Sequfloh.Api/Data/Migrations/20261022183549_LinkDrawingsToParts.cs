using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkDrawingsToParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PartNumber",
                schema: "sqf",
                table: "imported_files",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "drawing_part_links",
                schema: "sqf",
                columns: table => new
                {
                    DrawingId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drawing_part_links", x => new { x.DrawingId, x.PartId });
                    table.ForeignKey(
                        name: "FK_drawing_part_links_drawings_DrawingId",
                        column: x => x.DrawingId,
                        principalSchema: "sqf",
                        principalTable: "drawings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_drawing_part_links_parts_PartId",
                        column: x => x.PartId,
                        principalSchema: "sqf",
                        principalTable: "parts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tracking_record_drawings",
                schema: "sqf",
                columns: table => new
                {
                    TrackingRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    DrawingId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachedRevision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AttachedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracking_record_drawings", x => new { x.TrackingRecordId, x.DrawingId });
                    table.ForeignKey(
                        name: "FK_tracking_record_drawings_drawings_DrawingId",
                        column: x => x.DrawingId,
                        principalSchema: "sqf",
                        principalTable: "drawings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tracking_record_drawings_sequfloh_tracking_records_Tracking~",
                        column: x => x.TrackingRecordId,
                        principalSchema: "sqf",
                        principalTable: "sequfloh_tracking_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_drawing_part_links_PartId",
                schema: "sqf",
                table: "drawing_part_links",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_tracking_record_drawings_DrawingId",
                schema: "sqf",
                table: "tracking_record_drawings",
                column: "DrawingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "drawing_part_links",
                schema: "sqf");

            migrationBuilder.DropTable(
                name: "tracking_record_drawings",
                schema: "sqf");

            migrationBuilder.DropColumn(
                name: "PartNumber",
                schema: "sqf",
                table: "imported_files");
        }
    }
}
