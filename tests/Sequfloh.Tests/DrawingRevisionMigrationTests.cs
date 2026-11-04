using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Tests;

/// <summary>
/// Applies the drawing revision migration the way a live database would: drawings and their files are already there,
/// and the migration only records which file is current. Storage keys are not moved.
/// </summary>
public class DrawingRevisionMigrationTests
{
    const string Previous = "20261102183725_ChargebackLinkedWorkOrders";
    static readonly string Conn = TestApp.ConnectionString.Replace("Database=sequfloh_test", "Database=sequfloh_drawing_rev_migration");

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

    static async Task InsertAsync(NpgsqlConnection c, string table, Dictionary<string, string> values)
    {
        var cols = new List<(string Name, string Type)>();
        await using (var cmd = new NpgsqlCommand("SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'sqf' AND table_name = @t AND is_nullable = 'NO' AND column_default IS NULL AND is_identity = 'NO' AND column_name <> 'xmin'", c))
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
                    "timestamp with time zone" => "now()", "date" => "current_date", "jsonb" => "'[]'::jsonb", _ => "''"
                };
        await ExecAsync(c, $"INSERT INTO sqf.{table} ({string.Join(", ", all.Keys.Select(k => $"\"{k}\""))}) VALUES ({string.Join(", ", all.Values)})");
    }

    static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    [Fact]
    public async Task Existing_drawings_keep_their_files_and_gain_a_current_revision()
    {
        await using (var c = new NpgsqlConnection(Conn.Replace("Database=sequfloh_drawing_rev_migration", "Database=postgres")))
        {
            await c.OpenAsync();
            if (await ScalarAsync(c, "SELECT 1 FROM pg_database WHERE datname = 'sequfloh_drawing_rev_migration'") is null)
                await ExecAsync(c, "CREATE DATABASE sequfloh_drawing_rev_migration");
        }
        await using (var db = Context())
        {
            await db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS sqf CASCADE");
            await db.GetService<IMigrator>().MigrateAsync(Previous);
        }

        var site = Guid.NewGuid();
        var folder = Guid.NewGuid();
        var withLetter = Guid.NewGuid();
        var blankLetter = Guid.NewGuid();
        var blankFile = Guid.NewGuid();
        var role = Guid.NewGuid();
        await using (var c = new NpgsqlConnection(Conn))
        {
            await c.OpenAsync();
            await InsertAsync(c, "sites", new() { ["Id"] = Q(site.ToString()), ["Code"] = "'DR'", ["Name"] = "'Drawings'" });
            await InsertAsync(c, "drawing_folders", new() { ["Id"] = Q(folder.ToString()), ["Name"] = "'Plates'", ["SiteId"] = Q(site.ToString()) });
            await InsertAsync(c, "roles", new() { ["Id"] = Q(role.ToString()), ["Name"] = "'Built in'", ["IsSystem"] = "true" });
            await InsertAsync(c, "drawings", new() { ["Id"] = Q(withLetter.ToString()), ["FolderId"] = Q(folder.ToString()), ["Title"] = "'Plate'", ["CurrentRevision"] = "'C'", ["SiteId"] = Q(site.ToString()) });
            await InsertAsync(c, "drawings", new() { ["Id"] = Q(blankLetter.ToString()), ["FolderId"] = Q(folder.ToString()), ["Title"] = "'Blank letter'", ["CurrentRevision"] = "''", ["SiteId"] = Q(site.ToString()) });
            await InsertAsync(c, "drawings", new() { ["Id"] = Q(blankFile.ToString()), ["FolderId"] = Q(folder.ToString()), ["Title"] = "'Blank file'", ["CurrentRevision"] = "''", ["SiteId"] = Q(site.ToString()) });
            await RevisionAsync(c, withLetter, "A", "drawings/kept/a.pdf", "First cut");
            await RevisionAsync(c, withLetter, "C", "drawings/kept/c.pdf", null);
            await RevisionAsync(c, blankLetter, "B", "drawings/kept/b.pdf", null);
            await RevisionAsync(c, blankFile, "", "drawings/kept/blank.pdf", null);
        }

        await using (var db = Context()) await db.Database.MigrateAsync();

        await using (var c = new NpgsqlConnection(Conn))
        {
            await c.OpenAsync();
            Assert.Equal("C", await ScalarAsync(c, $"SELECT \"CurrentRevision\" FROM sqf.drawings WHERE \"Id\" = {Q(withLetter.ToString())}"));
            Assert.Equal(2, await ScalarAsync(c, $"SELECT \"Status\" FROM sqf.drawing_revisions WHERE \"DrawingId\" = {Q(withLetter.ToString())} AND \"Revision\" = 'A'"));
            Assert.Equal(1, await ScalarAsync(c, $"SELECT \"Status\" FROM sqf.drawing_revisions WHERE \"DrawingId\" = {Q(withLetter.ToString())} AND \"Revision\" = 'C'"));
            Assert.Equal("First cut", await ScalarAsync(c, $"SELECT \"Reason\" FROM sqf.drawing_revisions WHERE \"DrawingId\" = {Q(withLetter.ToString())} AND \"Revision\" = 'A'"));
            Assert.Equal("drawings/kept/a.pdf", await ScalarAsync(c, $"SELECT \"StorageKey\" FROM sqf.drawing_revisions WHERE \"DrawingId\" = {Q(withLetter.ToString())} AND \"Revision\" = 'A'"));
            Assert.Equal("drawings/kept/c.pdf", await ScalarAsync(c, $"SELECT \"StorageKey\" FROM sqf.drawing_revisions WHERE \"DrawingId\" = {Q(withLetter.ToString())} AND \"Revision\" = 'C'"));

            Assert.Equal("B", await ScalarAsync(c, $"SELECT \"CurrentRevision\" FROM sqf.drawings WHERE \"Id\" = {Q(blankLetter.ToString())}"));
            Assert.Equal(1, await ScalarAsync(c, $"SELECT \"Status\" FROM sqf.drawing_revisions WHERE \"DrawingId\" = {Q(blankLetter.ToString())} AND \"Revision\" = 'B'"));
            Assert.Equal("drawings/kept/b.pdf", await ScalarAsync(c, $"SELECT \"StorageKey\" FROM sqf.drawing_revisions WHERE \"DrawingId\" = {Q(blankLetter.ToString())} AND \"Revision\" = 'B'"));

            Assert.Equal("-", await ScalarAsync(c, $"SELECT \"CurrentRevision\" FROM sqf.drawings WHERE \"Id\" = {Q(blankFile.ToString())}"));
            Assert.Equal(1, await ScalarAsync(c, $"SELECT \"Status\" FROM sqf.drawing_revisions WHERE \"DrawingId\" = {Q(blankFile.ToString())} AND \"Revision\" = '-'"));
            Assert.Equal("drawings/kept/blank.pdf", await ScalarAsync(c, $"SELECT \"StorageKey\" FROM sqf.drawing_revisions WHERE \"DrawingId\" = {Q(blankFile.ToString())} AND \"Revision\" = '-'"));
            Assert.Equal(1L, (long)(await ScalarAsync(c, $"SELECT count(*) FROM sqf.role_permissions WHERE \"RoleId\" = {Q(role.ToString())} AND \"Permission\" = 'sequfloh.revise_drawings'"))!);
            Assert.Equal(1L, (long)(await ScalarAsync(c, $"SELECT count(*) FROM sqf.role_permissions WHERE \"RoleId\" = {Q(role.ToString())} AND \"Permission\" = 'sequfloh.release_drawings'"))!);
        }
    }

    static Task RevisionAsync(NpgsqlConnection c, Guid drawing, string revision, string key, string? notes) =>
        InsertAsync(c, "drawing_revisions", new()
        {
            ["Id"] = "gen_random_uuid()", ["DrawingId"] = Q(drawing.ToString()), ["Revision"] = Q(revision), ["FileName"] = "'plate.pdf'",
            ["ContentType"] = "'application/pdf'", ["StorageKey"] = Q(key), ["SizeBytes"] = "12", ["Sha256"] = "'abc'", ["CreatedByName"] = "'Pat Drafter'",
            ["Notes"] = notes is null ? "NULL" : Q(notes),
        });
}
