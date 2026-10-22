using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sequfloh.Infrastructure.Persistence;
using Xunit;

namespace Sequfloh.Tests;

/// <summary>
/// Runs the approval-removal migration against a database that still holds records in the old review, approval and sign-off states,
/// the way an existing live database would, and checks every record lands somewhere sensible with its history kept.
/// </summary>
public class SimplifyApprovalsMigrationTests
{
    static readonly string Conn = TestApp.ConnectionString.Replace("Database=sequfloh_test", "Database=sequfloh_migration_test");

    static AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(Conn, n => n.MigrationsHistoryTable("__ef_migrations", AppDbContext.Schema)).Options);

    static async Task ExecAsync(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
    static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>Inserts a row, filling every required column the test does not care about with a neutral value for its type.</summary>
    static async Task InsertAsync(NpgsqlConnection c, string table, Dictionary<string, string> values)
    {
        var cols = new List<(string Name, string Type)>();
        await using (var cmd = new NpgsqlCommand("SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'sqf' AND table_name = @t AND is_nullable = 'NO' AND column_default IS NULL AND is_identity = 'NO'", c))
        {
            cmd.Parameters.AddWithValue("t", table);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) cols.Add((r.GetString(0), r.GetString(1)));
        }
        var all = new Dictionary<string, string>(values);
        foreach (var (name, type) in cols)
            if (!all.ContainsKey(name))
                all[name] = type switch
                {
                    "uuid" => "gen_random_uuid()", "integer" or "bigint" or "smallint" or "numeric" or "double precision" => "0", "boolean" => "false",
                    "timestamp with time zone" => "now()", "date" => "current_date", "jsonb" => "'[]'::jsonb", _ => "'x'"
                };
        await ExecAsync(c, $"INSERT INTO sqf.{table} ({string.Join(", ", all.Keys.Select(k => $"\"{k}\""))}) VALUES ({string.Join(", ", all.Values)})");
    }
    static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    [Fact]
    public async Task Old_review_approval_and_signoff_states_map_to_the_simple_lifecycle_and_keep_their_history()
    {
        await using (var c = new NpgsqlConnection(Conn.Replace("Database=sequfloh_migration_test", "Database=postgres")))
        {
            await c.OpenAsync();
            if (await ScalarAsync(c, "SELECT 1 FROM pg_database WHERE datname = 'sequfloh_migration_test'") is null) await ExecAsync(c, "CREATE DATABASE sequfloh_migration_test");
        }
        await using (var db = Context())
        {
            await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS sqf CASCADE");
            await db.GetService<IMigrator>().MigrateAsync("20261022120703_Initial");
        }

        var site = Guid.NewGuid(); var user = Guid.NewGuid(); var role = Guid.NewGuid(); var adminRole = Guid.NewGuid(); var qualityRole = Guid.NewGuid();
        var wi = new Dictionary<int, Guid>(); var tr = new Dictionary<int, Guid>();
        await using (var c = new NpgsqlConnection(Conn))
        {
            await c.OpenAsync();
            await InsertAsync(c, "sites", new() { ["Id"] = Q(site.ToString()), ["Code"] = "'M1'", ["Name"] = "'Main'" });
            await InsertAsync(c, "users", new() { ["Id"] = Q(user.ToString()), ["Email"] = "'reviewer@example.test'", ["DisplayName"] = "'Robin Reviewer'" });
            await InsertAsync(c, "roles", new() { ["Id"] = Q(role.ToString()), ["Name"] = "'Doc control'" });
            await InsertAsync(c, "roles", new() { ["Id"] = Q(adminRole.ToString()), ["Name"] = "'Site admin'", ["IsSystem"] = "true" });
            await InsertAsync(c, "roles", new() { ["Id"] = Q(qualityRole.ToString()), ["Name"] = "'Quality folder'" });
            await ExecAsync(c, $"INSERT INTO sqf.role_permissions (\"RoleId\", \"Permission\") VALUES ({Q(qualityRole.ToString())}, 'sequfloh.edit_quality_folder')");
            foreach (var p in new[] { "sequfloh.approve_work_instruction", "sequfloh.review_work_instruction", "sequfloh.archive_work_instruction", "sequfloh.reject_task", "sequfloh.access" })
                await ExecAsync(c, $"INSERT INTO sqf.role_permissions (\"RoleId\", \"Permission\") VALUES ({Q(role.ToString())}, {Q(p)})");
            foreach (var status in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })
            {
                var id = Guid.NewGuid(); wi[status] = id;
                var v = new Dictionary<string, string> { ["Id"] = Q(id.ToString()), ["FamilyId"] = Q(id.ToString()), ["SiteId"] = Q(site.ToString()), ["Number"] = Q($"WI-{status}"), ["Revision"] = "'A'",
                    ["Status"] = status.ToString(), ["Content"] = "'{}'::jsonb" };
                if (status == 5) { v["ApprovedById"] = Q(user.ToString()); v["ApprovedAt"] = "now()"; v["ReviewedById"] = Q(user.ToString()); v["ReviewedAt"] = "now()"; }
                await InsertAsync(c, "sequfloh_work_instructions", v);
            }
            await ExecAsync(c, $"INSERT INTO sqf.sequfloh_work_instruction_transitions (\"Id\", \"WorkInstructionId\", \"Action\", \"FromStatus\", \"ToStatus\", \"Revision\", \"CreatedAt\", \"UpdatedAt\") VALUES (gen_random_uuid(), {Q(wi[5].ToString())}, 'approve', 4, 5, 'A', now(), now())");
            foreach (var status in new[] { 2, 5, 6, 7, 9 })
            {
                var id = Guid.NewGuid(); tr[status] = id;
                var v = new Dictionary<string, string> { ["Id"] = Q(id.ToString()), ["SiteId"] = Q(site.ToString()), ["WorkInstructionId"] = Q(wi[6].ToString()), ["Number"] = Q($"TR-{status}"),
                    ["Status"] = status.ToString(), ["ApprovalStatus"] = status == 9 ? "'Rejected'" : "'NotRequired'", ["Steps"] = "'[]'::jsonb", ["Characteristics"] = "'[]'::jsonb", ["PartsRows"] = "'[]'::jsonb",
                    ["Signoffs"] = status == 6 ? "'[{\"role\":\"Technician\",\"userName\":\"Terry Tech\",\"signedAt\":\"2026-10-01T10:00:00Z\",\"meaning\":\"Performed\"}]'::jsonb" : "'[{\"role\":\"Technician\",\"required\":true}]'::jsonb",
                    ["Details"] = "'{}'::jsonb" };
                if (status == 9) v["RejectionReason"] = "'Wrong seal'";
                await InsertAsync(c, "sequfloh_tracking_records", v);
            }
            await ExecAsync(c, "INSERT INTO sqf.sequfloh_reference_values (\"Id\", \"ListKey\", \"Code\", \"Label\", \"SortOrder\", \"IsActive\", \"IsDefault\", \"IsSeeded\", \"CreatedAt\", \"UpdatedAt\") VALUES " +
                "(gen_random_uuid(), 'approval-action-types', 'RELEASE', 'Release to floor', 10, true, true, true, now(), now()), (gen_random_uuid(), 'approval-action-types', 'APPROVE', 'Approve', 20, true, false, true, now(), now()), " +
                "(gen_random_uuid(), 'lifecycle-statuses', 'PENDING_REVIEW', 'Pending Review', 30, true, false, true, now(), now()), (gen_random_uuid(), 'rejection-reasons', 'OTHER', 'Other', 10, true, false, true, now(), now())");
        }

        await using (var db = Context()) await db.Database.MigrateAsync();

        await using (var c = new NpgsqlConnection(Conn))
        {
            await c.OpenAsync();
            async Task<int> WiStatus(int old) => (int)(await ScalarAsync(c, $"SELECT \"Status\" FROM sqf.sequfloh_work_instructions WHERE \"Id\" = {Q(wi[old].ToString())}"))!;
            async Task<int> TrStatus(int old) => (int)(await ScalarAsync(c, $"SELECT \"Status\" FROM sqf.sequfloh_tracking_records WHERE \"Id\" = {Q(tr[old].ToString())}"))!;
            foreach (var old in new[] { 0, 1, 2, 3, 4, 5 }) Assert.Equal(0, await WiStatus(old)); // everything short of release is a draft
            Assert.Equal(6, await WiStatus(6)); Assert.Equal(7, await WiStatus(7)); Assert.Equal(8, await WiStatus(8));
            Assert.Equal(8, await WiStatus(9)); // archived -> obsolete
            Assert.NotNull(await ScalarAsync(c, $"SELECT \"ObsoleteAt\" FROM sqf.sequfloh_work_instructions WHERE \"Id\" = {Q(wi[9].ToString())}"));
            Assert.Equal(0L, (long)(await ScalarAsync(c, "SELECT count(*) FROM sqf.sequfloh_work_instruction_transitions WHERE \"ToStatus\" IN (1,2,3,4,5,9) OR \"FromStatus\" IN (1,2,3,4,5,9)"))!);

            Assert.Equal(2, await TrStatus(2)); Assert.Equal(7, await TrStatus(5)); Assert.Equal(7, await TrStatus(6)); Assert.Equal(7, await TrStatus(7));
            Assert.Equal(8, await TrStatus(9));
            Assert.Equal("Wrong seal", await ScalarAsync(c, $"SELECT \"FailureReason\" FROM sqf.sequfloh_tracking_records WHERE \"Id\" = {Q(tr[9].ToString())}"));
            Assert.NotNull(await ScalarAsync(c, $"SELECT \"ActualCompletion\" FROM sqf.sequfloh_tracking_records WHERE \"Id\" = {Q(tr[6].ToString())}"));

            // the history of who reviewed, approved and signed is in the audit trail
            var wiAudit = (string)(await ScalarAsync(c, $"SELECT \"Description\" FROM sqf.audit_entries WHERE \"EntityId\" = {Q(wi[5].ToString())}"))!;
            Assert.Contains("reviewed by Robin Reviewer", wiAudit);
            Assert.Contains("approved by Robin Reviewer", wiAudit);
            var trAudit = (string)(await ScalarAsync(c, $"SELECT \"Description\" FROM sqf.audit_entries WHERE \"EntityId\" = {Q(tr[6].ToString())}"))!;
            Assert.Contains("Technician signed by Terry Tech", trAudit);
            Assert.Contains("Rejection reason: Wrong seal", (string)(await ScalarAsync(c, $"SELECT \"Description\" FROM sqf.audit_entries WHERE \"EntityId\" = {Q(tr[9].ToString())}"))!);
            Assert.Null(await ScalarAsync(c, $"SELECT 1 FROM sqf.audit_entries WHERE \"EntityId\" = {Q(tr[2].ToString())}")); // untouched records get no entry

            // the role that reviewed and approved can now release; removed permissions are gone
            var perms = new List<string>();
            await using (var cmd = new NpgsqlCommand($"SELECT \"Permission\" FROM sqf.role_permissions WHERE \"RoleId\" = {Q(role.ToString())}", c))
            await using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) perms.Add(r.GetString(0));
            Assert.Equal(["sequfloh.access", "sequfloh.obsolete_work_instruction", "sequfloh.release_work_instruction"], perms.Order().ToList());
            // imported files: granted from what roles already held
            async Task<List<string>> PermsOf(Guid r)
            {
                var list = new List<string>();
                await using var cmd = new NpgsqlCommand($"SELECT \"Permission\" FROM sqf.role_permissions WHERE \"RoleId\" = {Q(r.ToString())}", c);
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync()) list.Add(rd.GetString(0));
                return list;
            }
            Assert.Equal(["sequfloh.delete_records", "sequfloh.import_files", "sequfloh.manage_drawings", "sequfloh.view_drawings", "sequfloh.view_imported_files"], (await PermsOf(adminRole)).Order().ToList());
            Assert.Equal(["sequfloh.edit_quality_folder", "sequfloh.import_files", "sequfloh.view_imported_files"], (await PermsOf(qualityRole)).Order().ToList());

            // reference lists: the kept action keeps its custom label under the new list; removed entries are gone
            Assert.Equal("Release to floor", await ScalarAsync(c, "SELECT \"Label\" FROM sqf.sequfloh_reference_values WHERE \"ListKey\" = 'lifecycle-actions' AND \"Code\" = 'RELEASE'"));
            Assert.Equal(0L, (long)(await ScalarAsync(c, "SELECT count(*) FROM sqf.sequfloh_reference_values WHERE \"ListKey\" IN ('approval-action-types', 'rejection-reasons') OR \"Code\" = 'PENDING_REVIEW'"))!);
            Assert.Null(await ScalarAsync(c, "SELECT 1 FROM information_schema.columns WHERE table_schema = 'sqf' AND column_name IN ('Signoffs', 'ApprovalStatus', 'ReviewerId', 'ApproverId')"));
        }
    }
}
