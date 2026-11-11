using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ManagerId",
                schema: "sqf",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_ManagerId",
                schema: "sqf",
                table: "users",
                column: "ManagerId");

            migrationBuilder.AddForeignKey(
                name: "FK_users_users_ManagerId",
                schema: "sqf",
                table: "users",
                column: "ManagerId",
                principalSchema: "sqf",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_users_users_ManagerId",
                schema: "sqf",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_ManagerId",
                schema: "sqf",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ManagerId",
                schema: "sqf",
                table: "users");
        }
    }
}
