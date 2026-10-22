using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Common;

namespace Sequfloh.Infrastructure.Persistence;

/// <summary>
/// Stamps created/updated metadata and writes audit entries in the same transaction as the change,
/// so a record change is never saved without its audit entry.
/// </summary>
public sealed class AuditInterceptor(ICurrentUser user, IClock clock) : SaveChangesInterceptor
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Entity, string> Notes = new();

    /// <summary>Extra words for the audit entry of a record being deleted (what else went with it), added after the record label.</summary>
    public static void Note(Entity record, string text) => Notes.AddOrUpdate(record, text);

    private static readonly HashSet<string> Ignored = [nameof(Entity.RowVersion), nameof(Entity.UpdatedAt), nameof(Entity.UpdatedById), nameof(Entity.CreatedAt), nameof(Entity.CreatedById)];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (e.Context is not null) await ApplyAsync(e.Context, true, ct);
        return await base.SavingChangesAsync(e, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData e, InterceptionResult<int> result)
    {
        // the synchronous path runs only synchronous queries, so the task is already complete
        if (e.Context is not null) ApplyAsync(e.Context, false, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavingChanges(e, result);
    }

    private sealed record Pending(EntityEntry<Entity> Entry, string Action, string Label, List<(string Label, object? From, object? To, string? Fixed)> Fields, Dictionary<string, object?> Changes);

    private async Task ApplyAsync(DbContext ctx, bool async, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var entries = ctx.ChangeTracker.Entries<Entity>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        var audits = new List<AuditEntry>();
        var pending = new List<Pending>();
        foreach (var entry in entries.Where(x => x.Entity is IAppendOnly && x.State is EntityState.Modified or EntityState.Deleted))
            throw new AppException($"{entry.Metadata.ClrType.Name} entries are append-only; post a correcting entry instead.", 409);
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.CreatedById = user.UserId;
            }
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                entry.Entity.UpdatedById = user.UserId;
            }
            if (entry.Entity is not IAudited audited) continue;

            var changes = Changes(entry);
            if (entry.State == EntityState.Modified && changes.Count == 0) continue;
            var action = entry.State switch
            {
                EntityState.Added => "Created",
                EntityState.Deleted => "Deleted",
                _ when changes.ContainsKey(nameof(IArchivable.IsArchived)) => (bool?)entry.Property(nameof(IArchivable.IsArchived)).CurrentValue == true ? "Archived" : "Restored",
                _ => "Updated"
            };
            var fields = entry.State == EntityState.Modified ? Fields(entry) : [];
            if (entry.State == EntityState.Deleted && Notes.TryGetValue(entry.Entity, out var note)) fields.Add(("", null, null, note));
            pending.Add(new Pending(entry, action, audited.AuditLabel, fields, changes));
        }
        if (pending.Count > 0)
        {
            var ids = new HashSet<Guid>(pending.SelectMany(p => AuditText.Ids(p.Label)
                .Concat(p.Fields.SelectMany(f => new[] { f.From, f.To }).OfType<Guid>())));
            var names = ids.Count == 0 ? new Dictionary<Guid, string>() : await NamesAsync(ctx, ids, async, ct);
            string? Name(Guid id) => names.TryGetValue(id, out var n) ? n : null;
            foreach (var p in pending)
            {
                var label = AuditText.Resolve(p.Label, Name);
                var detail = p.Fields.Count == 0 ? "" : ": " + string.Join("; ", p.Fields.Select(f => f.Fixed ?? AuditText.Change(f.Label, f.From, f.To, Name)));
                audits.Add(new AuditEntry
                {
                    OccurredAt = now,
                    UserId = user.UserId,
                    UserName = user.UserName,
                    Action = p.Action,
                    EntityType = p.Entry.Metadata.ClrType.Name,
                    EntityId = p.Entry.Entity.Id,
                    Description = AuditText.Fit($"{p.Action} {label}{detail}"),
                    Changes = p.Changes.Count == 0 ? null : JsonSerializer.Serialize(p.Changes),
                    SiteId = p.Entry.Entity is ISiteScoped s ? s.SiteId : null,
                    CorrelationId = user.CorrelationId
                });
            }
        }
        if (audits.Count > 0) ctx.Set<AuditEntry>().AddRange(audits);
    }

    /// <summary>Changed fields with readable labels, for the description. Secrets and binary content never show their value.</summary>
    private static List<(string Label, object? From, object? To, string? Fixed)> Fields(EntityEntry<Entity> entry)
    {
        var list = new List<(string, object?, object?, string?)>();
        foreach (var p in entry.Properties)
        {
            if (Ignored.Contains(p.Metadata.Name) || !p.IsModified || Equals(p.OriginalValue, p.CurrentValue)) continue;
            var label = AuditText.Label(p.Metadata.Name, p.Metadata.ClrType);
            if (IsSecret(p.Metadata.Name)) { list.Add((label, null, null, $"{label} changed (value not recorded)")); continue; }
            if (p.Metadata.ClrType == typeof(byte[])) { list.Add((label, null, null, $"{label} replaced")); continue; }
            if (p.Metadata.Name == nameof(IArchivable.IsArchived)) continue; // the action already says Archived or Restored
            list.Add((label, p.OriginalValue, p.CurrentValue, null));
        }
        return list;
    }

    private static bool IsSecret(string name) =>
        name.StartsWith("Encrypted", StringComparison.Ordinal) || name.EndsWith("SecretHash", StringComparison.Ordinal) || name.EndsWith("Ciphertext", StringComparison.Ordinal);

    /// <summary>
    /// Readable names for ids found in labels and changed fields: people, roles, sites and departments by name, records saved in the
    /// same change by their label, and other records by the label they were created with.
    /// </summary>
    private static async Task<Dictionary<Guid, string>> NamesAsync(DbContext ctx, HashSet<Guid> ids, bool async, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        async Task Add<T>(IQueryable<T> q, Func<T, Guid> id, Func<T, string> name)
        {
            foreach (var x in async ? await q.ToListAsync(ct) : q.ToList())
                if (!names.ContainsKey(id(x)) && name(x) is { Length: > 0 } n) names[id(x)] = n;
        }
        var open = () => ids.Where(i => !names.ContainsKey(i)).ToList();
        await Add(ctx.Set<Identity.ApplicationUser>().AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.DisplayName }), x => x.Id, x => x.DisplayName);
        if (open().Count > 0) await Add(ctx.Set<Domain.Security.Role>().AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.Name }), x => x.Id, x => x.Name);
        if (open().Count > 0) await Add(ctx.Set<Domain.Organization.Site>().AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.Code }), x => x.Id, x => x.Code);
        if (open().Count > 0) await Add(ctx.Set<Domain.Organization.Department>().AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.Code }), x => x.Id, x => x.Code);
        foreach (var e in ctx.ChangeTracker.Entries<Entity>())
            if (e.Entity is IAudited a && ids.Contains(e.Entity.Id) && !names.ContainsKey(e.Entity.Id))
                names[e.Entity.Id] = AuditText.RefPattern().Replace(a.AuditLabel, m => m.Groups[2].Value);
        if (open() is { Count: > 0 } rest)
        {
            var q = ctx.Set<AuditEntry>().AsNoTracking().Where(a => rest.Contains(a.EntityId) && a.Action == "Created").Select(a => new { a.EntityId, a.Description });
            await Add(q, x => x.EntityId, x => x.Description.StartsWith("Created ") ? x.Description[8..] : "");
        }
        return names;
    }

    private static Dictionary<string, object?> Changes(EntityEntry<Entity> entry)
    {
        var d = new Dictionary<string, object?>();
        foreach (var p in entry.Properties)
        {
            if (Ignored.Contains(p.Metadata.Name)) continue;
            // Encrypted secrets and secret hashes: record that they changed, never the value.
            if (IsSecret(p.Metadata.Name))
            {
                if (entry.State == EntityState.Added ? p.CurrentValue is not null : entry.State == EntityState.Deleted || (p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)))
                    d[p.Metadata.Name] = p.CurrentValue is null ? "cleared" : "set (value not recorded)";
                continue;
            }
            // binary content (label images and similar): record the size, not the bytes
            if (p.Metadata.ClrType == typeof(byte[]))
            {
                if (entry.State == EntityState.Added ? p.CurrentValue is not null : entry.State == EntityState.Deleted || (p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)))
                    d[p.Metadata.Name] = p.CurrentValue is byte[] b ? $"{b.Length} bytes" : "cleared";
                continue;
            }
            switch (entry.State)
            {
                case EntityState.Added:
                    if (p.CurrentValue is not null) d[p.Metadata.Name] = new { to = p.CurrentValue };
                    break;
                case EntityState.Deleted:
                    d[p.Metadata.Name] = new { from = p.OriginalValue };
                    break;
                case EntityState.Modified when p.IsModified && !Equals(p.OriginalValue, p.CurrentValue):
                    // enums keep their stored number and add the readable name
                    d[p.Metadata.Name] = p.OriginalValue is Enum || p.CurrentValue is Enum
                        ? new { from = p.OriginalValue, to = p.CurrentValue, fromName = (p.OriginalValue as Enum)?.ToString(), toName = (p.CurrentValue as Enum)?.ToString() }
                        : new { from = p.OriginalValue, to = p.CurrentValue };
                    break;
            }
        }
        return d;
    }
}
