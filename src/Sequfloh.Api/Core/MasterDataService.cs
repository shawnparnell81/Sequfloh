using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Domain.MasterData;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Modules.Quality;
using Sequfloh.Infrastructure.Persistence;

namespace Sequfloh.Infrastructure.Modules.MasterData;

public sealed record SavePartRequest(string PartNumber, string Name, string? Description, PartType PartType, string Revision, string UnitOfMeasure, Guid? ProductId,
    string? Material, bool HasSpecialCharacteristics, Guid SiteId, Guid? DepartmentId, MasterStatus Status, uint RowVersion = 0);
public sealed record RevisePartRequest(string Revision, string Reason, DateOnly? EffectiveOn);

/// <summary>Part master: part numbers, revisions and their history.</summary>
public sealed class MasterDataService(AppDbContext db, ModuleSupport m)
{
    public const string PartRecordType = "Part";

    private static string Code(string? v, string label, int max)
    {
        ModuleSupport.Required(v, label, max);
        return v!.Trim();
    }

    private static string? Opt(string? v, string label, int max)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        if (v.Length > max) throw new AppException($"{label} is limited to {max} characters.");
        return v.Trim();
    }

    public async Task<Part> SavePartAsync(Guid? id, SavePartRequest r, CancellationToken ct)
    {
        await m.RequireAsync(Permissions.MasterDataEdit, r.SiteId, r.DepartmentId, ct);
        await m.ValidateLocationAsync(r.SiteId, r.DepartmentId, ct);
        var number = Code(r.PartNumber, "Part number", 60);
        // a part may keep an OR # it shares with other parts (from an import); a new or changed number must not be in use
        var keeps = id is not null && await db.Parts.AnyAsync(p => p.Id == id && p.PartNumber == number, ct);
        if (!keeps && await db.Parts.AnyAsync(p => p.PartNumber == number && p.Id != id, ct)) throw new ConflictException("A part with this number already exists.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        Part p;
        if (id is null)
        {
            p = new Part { Revision = Code(r.Revision, "Revision", 10) };
            db.Parts.Add(p);
            db.PartRevisions.Add(new PartRevision { PartId = p.Id, Revision = p.Revision, Reason = "Initial release", EffectiveOn = await m.TodayAsync(r.SiteId, ct) });
        }
        else
        {
            p = await db.Parts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Part");
            await m.RequireAsync(Permissions.MasterDataEdit, p.SiteId, p.DepartmentId, ct);
            db.Entry(p).Property(x => x.RowVersion).OriginalValue = r.RowVersion;
            if (!string.Equals(r.Revision?.Trim(), p.Revision, StringComparison.Ordinal)) throw new AppException("Use Revise to change the part revision; history is kept.");
        }
        p.PartNumber = number; p.Name = Code(r.Name, "Name", 200); p.Description = Opt(r.Description, "Description", 4000); p.PartType = r.PartType;
        p.UnitOfMeasure = Code(r.UnitOfMeasure, "Unit of measure", 20); p.ProductId = r.ProductId; p.Material = Opt(r.Material, "Material", 200);
        p.HasSpecialCharacteristics = r.HasSpecialCharacteristics; p.SiteId = r.SiteId; p.DepartmentId = r.DepartmentId; p.Status = r.Status;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return p;
    }

    public async Task RevisePartAsync(Guid id, RevisePartRequest r, CancellationToken ct)
    {
        var p = await db.Parts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Part");
        await m.RequireAsync(Permissions.MasterDataEdit, p.SiteId, p.DepartmentId, ct);
        var rev = Code(r.Revision, "Revision", 10);
        ModuleSupport.Required(r.Reason, "Reason", 1000);
        if (await db.PartRevisions.AnyAsync(x => x.PartId == id && x.Revision == rev, ct)) throw new ConflictException("That revision already exists for this part.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.PartRevisions.Add(new PartRevision { PartId = id, Revision = rev, Reason = r.Reason.Trim(), EffectiveOn = r.EffectiveOn ?? await m.TodayAsync(p.SiteId, ct) });
        p.Revision = rev;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
