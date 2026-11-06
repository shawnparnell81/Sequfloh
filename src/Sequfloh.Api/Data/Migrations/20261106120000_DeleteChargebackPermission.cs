using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sequfloh.Infrastructure.Persistence;

#nullable disable

namespace Sequfloh.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261106120000_DeleteChargebackPermission")]
    public partial class DeleteChargebackPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // the built-in Administrator role holds every permission, including Delete chargeback reports
            migrationBuilder.Sql("""
                INSERT INTO sqf.role_permissions ("RoleId", "Permission")
                SELECT "Id", 'sequfloh.delete_chargebacks' FROM sqf.roles WHERE "IsSystem"
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM sqf.role_permissions WHERE "Permission" = 'sequfloh.delete_chargebacks';""");
        }
    }
}
