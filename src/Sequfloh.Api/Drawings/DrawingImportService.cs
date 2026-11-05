using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;
using Sequfloh.Application.Security;
using Sequfloh.Domain.Audit;
using Sequfloh.Domain.Drawings;
using Sequfloh.Domain.Security;
using Sequfloh.Infrastructure.Persistence;
using Sequfloh.Infrastructure.Services;
using Sequfloh.Platform;

namespace Sequfloh.Drawings;

/// <summary>
/// Bulk import of a folder of drawings. The browser sends the folder tree and file list for a preview, creates the folders, then uploads each file
/// straight to file storage with a signed address and confirms it here; only names, sizes and keys reach this server. A file already imported to the
/// same folder with the same name and size is skipped, so picking the same folder again carries on where it stopped.
/// </summary>
public sealed class DrawingImportService(AppDbContext db, IFileStorage storage, IAccessService access, ICurrentUser me, AuthSettings auth, DrawingService drawings, DrawingPartMatcher matcher)
{
    /// <summary>Largest file a bulk import takes.</summary>
    public const long MaxBytes = 5L * 1024 * 1024 * 1024;
    public const int MaxBatch = 2000;
    static readonly TimeSpan TokenLife = TimeSpan.FromHours(12);

    public sealed record FileItem(string Path, long Size);
    public sealed record PlanRequest(Guid? FolderId, List<FileItem>? Files, List<string>? Folders, bool LinkParts);
    public sealed record FoldersRequest(Guid? FolderId, List<string>? Paths, bool LinkParts);
    public sealed record StartRequest(Guid FolderId, string FileName, long Size);
    public sealed record FinishRequest(string Token, string? UploadId, List<UploadedPart>? Parts, string? Revision, bool LinkParts, List<string>? MatchNames);
    public sealed record AbortRequest(string Token, string? UploadId);
    public sealed record SummaryRequest(Guid? FolderId, int Imported, int Folders, long Bytes, int Skipped, int Failed, int Linked, bool Cancelled, int Conflicts = 0);

    Guid UserId => me.UserId ?? throw new ForbiddenException();

    /// <summary>Where an import goes: a folder, or the top level (All folders) of a site when Id is null.</summary>
    sealed record Root(Guid? Id, Guid SiteId, Guid? DepartmentId, string Name);

    async Task<Root> RootAsync(Guid? folderId, CancellationToken ct)
    {
        if (folderId is { } id) { var f = await ManagedFolderAsync(id, ct); return new Root(f.Id, f.SiteId, f.DepartmentId, f.Name); }
        return new Root(null, await drawings.TopLevelSiteAsync(null, ct), null, "All folders");
    }

    async Task<DrawingFolder> ManagedFolderAsync(Guid id, CancellationToken ct)
    {
        var f = await db.DrawingFolders.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Folder");
        if (!await access.CanAccessAsync(UserId, Permissions.SequflohViewDrawings, f.SiteId, f.DepartmentId, ct)) throw new NotFoundException("Folder");
        if (!await access.CanAccessAsync(UserId, Permissions.SequflohManageDrawings, f.SiteId, f.DepartmentId, ct)) throw new ForbiddenException();
        if (f.IsArchived) throw new AppException("The folder is archived. Restore it first.");
        return f;
    }

    /// <summary>
    /// "a/b/c.pdf" split into clean parts. A path that climbs out of the folder (".."), starts at the root, or hides a null is rejected.
    /// Empty pieces from a double slash are dropped. Nothing is rewritten into a different folder.
    /// </summary>
    public static string[] Segments(string? path)
    {
        var raw = (path ?? "").Replace('\\', '/');
        if (raw.Contains('\0') || raw.StartsWith('/') || raw.Contains(':'))
            throw new AppException("That path is not allowed. Folder traversal is not permitted.");
        var parts = raw.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p is "." or ".."))
            throw new AppException("That path is not allowed. Folder traversal is not permitted.");
        var kept = parts.Where(p => p.Length > 0).ToArray();
        if (string.Join('/', kept).Length > 950 || (kept.LastOrDefault()?.Length ?? 0) > 255)
            throw new AppException("That file path or name is too long.");
        return kept;
    }

    static string Key(string[] segs) => string.Join("/", segs).ToUpperInvariant();

    /// <summary>The folders under a folder that are not archived, by path from it ("" is the folder itself).</summary>
    async Task<Dictionary<string, Guid?>> TreeAsync(Root root, CancellationToken ct)
    {
        var map = new Dictionary<string, Guid?> { [""] = root.Id };
        var level = new List<(Guid Id, string Path)>();
        if (root.Id is { } rid) level.Add((rid, ""));
        else
            foreach (var k in await db.DrawingFolders.AsNoTracking().Where(f => f.ParentId == null && f.SiteId == root.SiteId && !f.IsArchived).Select(f => new { f.Id, f.Name }).ToListAsync(ct))
                if (map.TryAdd(k.Name.ToUpperInvariant(), k.Id)) level.Add((k.Id, k.Name.ToUpperInvariant()));
        while (level.Count > 0 && map.Count < 200_000)
        {
            var ids = level.Select(x => x.Id).ToList();
            var kids = await db.DrawingFolders.AsNoTracking().Where(f => f.ParentId != null && ids.Contains(f.ParentId.Value) && !f.IsArchived).Select(f => new { f.Id, f.ParentId, f.Name }).ToListAsync(ct);
            var paths = level.ToDictionary(x => x.Id, x => x.Path);
            level = [];
            foreach (var k in kids)
            {
                var path = (paths[k.ParentId!.Value].Length == 0 ? "" : paths[k.ParentId.Value] + "/") + k.Name.ToUpperInvariant();
                if (map.TryAdd(path, k.Id)) level.Add((k.Id, path));
            }
        }
        return map;
    }

    /// <summary>Parts matching the names (see DrawingPartMatcher), by the normalized name.</summary>
    async Task<Dictionary<string, List<(Guid Id, string PartNumber)>>> PartsByNameAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var wanted = names.Select(DrawingPartMatcher.Normalize).Where(n => n.Length > 0).ToHashSet();
        if (wanted.Count == 0) return [];
        var index = await matcher.IndexAsync(ct);
        return wanted.Where(index.ContainsKey).ToDictionary(n => n, n => index[n].Select(x => (x.Id, x.PartNumber)).ToList());
    }

    static string Title(string fileName) => Path.GetFileNameWithoutExtension(fileName).Trim();

    /// <summary>Drawing revisions recorded on parts named by these folder or file names. File names are not parsed for a revision letter.</summary>
    async Task<List<string?>> RevisionsForNamesAsync(IEnumerable<string> names, CancellationToken ct)
    {
        var index = await matcher.IndexAsync(ct);
        var hits = DrawingPartMatcher.Match(index, names).Select(x => x.Id).Distinct().ToList();
        if (hits.Count == 0) return [];
        return await db.Parts.AsNoTracking().Where(p => hits.Contains(p.Id)).Select(p => p.DrawingRevision).ToListAsync(ct);
    }

    /// <summary>What an import of these files would do: which files are already here, which folders are new and which names match parts.</summary>
    public async Task<object> PlanAsync(PlanRequest r, CancellationToken ct)
    {
        var root = await RootAsync(r.FolderId, ct);
        var files = r.Files ?? [];
        if (files.Count > MaxBatch) throw new AppException($"Send at most {MaxBatch} files at a time.");
        var tree = await TreeAsync(root, ct);
        var folderIds = tree.Values.OfType<Guid>().ToList();
        var have = new Dictionary<(Guid, string), List<long>>();
        foreach (var chunk in folderIds.Chunk(5000))
        {
            var rows = await db.DrawingRevisions.AsNoTracking().Join(db.Drawings.Where(d => chunk.Contains(d.FolderId) && !d.IsArchived), v => v.DrawingId, d => d.Id, (v, d) => new { d.FolderId, v.FileName, v.SizeBytes })
                .ToListAsync(ct);
            foreach (var x in rows)
            {
                var k = (x.FolderId, x.FileName.ToUpperInvariant());
                if (!have.TryGetValue(k, out var sizes)) have[k] = sizes = [];
                sizes.Add(x.SizeBytes);
            }
        }
        var folderPaths = new List<string[]>();
        var folderRejected = new List<(string Path, string Reason)>();
        foreach (var raw in r.Folders ?? [])
        {
            try { var s = Segments(raw); if (s.Length > 0) folderPaths.Add(s); }
            catch (AppException ex) { folderRejected.Add((raw, ex.Message)); }
        }
        var names = new List<string>();
        foreach (var f in files)
        {
            try { var s = Segments(f.Path); if (s.Length > 0) names.Add(Title(s[^1])); }
            catch (AppException) { /* reported on the file row */ }
        }
        names.AddRange(folderPaths.Select(s => s[^1]));
        var parts = r.LinkParts ? await PartsByNameAsync(names, ct) : [];
        List<string> Match(string name) => parts.TryGetValue(DrawingPartMatcher.Normalize(name), out var l) ? l.Select(x => x.PartNumber).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList() : [];
        return new
        {
            Files = files.Select(f =>
            {
                try
                {
                    var s = Segments(f.Path);
                    long[] sizes = [];
                    if (s.Length > 0 && tree.TryGetValue(Key(s[..^1]), out var fid) && fid is { } fv && have.TryGetValue((fv, s[^1].ToUpperInvariant()), out var found)) sizes = [.. found];
                    var exists = sizes.Contains(f.Size);
                    var conflict = sizes.Length > 0 && !exists;
                    return new { f.Path, Exists = exists, Conflict = conflict, Rejected = false, Reason = conflict ? "A file with this name is already in this folder and its contents differ. Nothing was overwritten." : (string?)null, Parts = s.Length > 0 ? Match(Title(s[^1])) : new List<string>() };
                }
                catch (AppException ex)
                {
                    return new { f.Path, Exists = false, Conflict = false, Rejected = true, Reason = (string?)ex.Message, Parts = new List<string>() };
                }
            }),
            Folders = folderPaths.Select(s => new { Path = string.Join("/", s), Exists = tree.ContainsKey(Key(s)), Conflict = false, Rejected = false, Reason = (string?)null, Parts = Match(s[^1]) })
                .Concat(folderRejected.Select(x => new { Path = x.Path, Exists = false, Conflict = false, Rejected = true, Reason = (string?)x.Reason, Parts = new List<string>() })),
        };
    }

    /// <summary>Creates the folders that are not there yet (parents first) and returns every folder's id by path.</summary>
    public async Task<Dictionary<string, Guid>> FoldersAsync(FoldersRequest r, CancellationToken ct)
    {
        var root = await RootAsync(r.FolderId, ct);
        var paths = (r.Paths ?? []).Select(Segments).Where(s => s.Length > 0).ToList();
        if (paths.Count > MaxBatch) throw new AppException($"Send at most {MaxBatch} folders at a time.");
        var tree = await TreeAsync(root, ct);
        var all = paths.SelectMany(s => Enumerable.Range(1, s.Length).Select(n => s[..n])).DistinctBy(Key).OrderBy(s => s.Length).ToList();
        var parts = r.LinkParts ? await PartsByNameAsync(all.Where(s => !tree.ContainsKey(Key(s))).Select(s => s[^1]), ct) : [];
        foreach (var s in all)
        {
            if (tree.ContainsKey(Key(s))) continue;
            var name = DrawingService.Clean(s[^1], 200, "a folder name");
            var f = new DrawingFolder { Name = name, ParentId = tree[Key(s[..^1])], SiteId = root.SiteId, DepartmentId = root.DepartmentId,
                PartId = parts.TryGetValue(DrawingPartMatcher.Normalize(name), out var l) ? l.OrderBy(x => x.PartNumber, StringComparer.Ordinal).First().Id : null };
            db.DrawingFolders.Add(f);
            tree[Key(s)] = f.Id;
        }
        await db.SaveChangesAsync(ct);
        return paths.ToDictionary(s => string.Join("/", s), s => tree[Key(s)]!.Value);
    }

    // ---- one file -------------------------------------------------------------------------------------------------------------------

    sealed record Ticket(Guid User, Guid Folder, Guid Drawing, Guid Revision, string FileName, long Size, long Expires)
    {
        public string Ext => Path.GetExtension(FileName).ToLowerInvariant();
        public string StorageKey => $"drawings/{Drawing:N}/{Revision:N}{Ext}";
    }

    string Sign(string body) => Convert.ToBase64String(HMACSHA256.HashData(auth.Key.Key, Encoding.UTF8.GetBytes("drawing-import|" + body)));

    string Issue(Ticket t)
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(t)));
        return body + "." + Sign(body);
    }

    Ticket Read(string? token)
    {
        var parts = (token ?? "").Split('.');
        if (parts.Length != 2 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(parts[0])), Encoding.UTF8.GetBytes(parts[1])))
            throw new AppException("The upload was not recognised. Start the import again.");
        var t = System.Text.Json.JsonSerializer.Deserialize<Ticket>(Encoding.UTF8.GetString(Convert.FromBase64String(parts[0])))!;
        if (t.User != UserId) throw new ForbiddenException();
        if (DateTimeOffset.FromUnixTimeSeconds(t.Expires) < DateTimeOffset.UtcNow) throw new AppException("The upload took too long. Pick the folder again to carry on.");
        return t;
    }

    /// <summary>Where the browser uploads one file. Nothing is recorded until the upload is confirmed with FinishAsync.</summary>
    public async Task<object> StartAsync(StartRequest r, CancellationToken ct)
    {
        await ManagedFolderAsync(r.FolderId, ct);
        var name = Path.GetFileName(r.FileName ?? "");
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (!DrawingService.Accepted.Contains(ext)) throw new AppException($"{name}: drawings can be PDF, PNG, JPG, TIF, DWG, DXF, STEP, IGES, SolidWorks or Word files.");
        if (r.Size <= 0) throw new AppException($"{name} is empty.");
        if (r.Size > MaxBytes) throw new AppException($"{name} is larger than 5 GB.");
        var t = new Ticket(UserId, r.FolderId, Guid.CreateVersion7(), Guid.CreateVersion7(), name, r.Size, DateTimeOffset.UtcNow.Add(TokenLife).ToUnixTimeSeconds());
        var token = Issue(t);
        var plan = await storage.BeginDirectAsync(t.StorageKey, DrawingService.TypeOf(ext), r.Size, ct);
        if (plan is null) return new { Token = token, Url = "/api/drawings/import/file?token=" + Uri.EscapeDataString(token), UploadId = (string?)null, PartSize = r.Size, PartUrls = Array.Empty<string>(), Direct = false, ContentType = DrawingService.TypeOf(ext) };
        return new { Token = token, plan.Url, plan.UploadId, plan.PartSize, plan.PartUrls, Direct = true, ContentType = DrawingService.TypeOf(ext) };
    }

    /// <summary>Local-disk storage only: the file body for a started upload (file storage in the cloud takes the upload directly instead).</summary>
    public async Task ReceiveAsync(string token, Stream body, CancellationToken ct)
    {
        if (storage is not LocalFileStorage) throw new AppException("Upload the file to file storage directly.");
        var t = Read(token);
        await ManagedFolderAsync(t.Folder, ct);
        await storage.PutAsync(t.StorageKey, body, DrawingService.TypeOf(t.Ext), ct);
    }

    public async Task AbortAsync(AbortRequest r, CancellationToken ct)
    {
        var t = Read(r.Token);
        if (await db.DrawingRevisions.AnyAsync(v => v.Id == t.Revision, ct)) return;
        await storage.AbortDirectAsync(t.StorageKey, r.UploadId, ct);
    }

    /// <summary>Records an uploaded file as a drawing (title from the file name) once the stored file is there with the right size and type.</summary>
    public async Task<object> FinishAsync(FinishRequest r, CancellationToken ct)
    {
        var t = Read(r.Token);
        var folder = await ManagedFolderAsync(t.Folder, ct);
        if (await db.DrawingRevisions.AnyAsync(v => v.Id == t.Revision, ct)) return new { Id = t.Drawing, Skipped = false, Linked = 0 };
        if (r.UploadId is { Length: > 0 } uid) await storage.CompleteDirectAsync(t.StorageKey, uid, r.Parts ?? [], ct);
        string rev;
        var stored = await storage.PeekAsync(t.StorageKey, ct) ?? throw new AppException($"{t.FileName} did not reach file storage. Try again.");
        if (stored.Size != t.Size || !DrawingService.Matches(stored.Start, t.Ext))
        {
            await storage.DeleteAsync(t.StorageKey, ct);
            throw new AppException(stored.Size != t.Size ? $"{t.FileName} did not upload completely. Try again." : $"{t.FileName} does not look like a {t.Ext.TrimStart('.').ToUpperInvariant()} file.");
        }
        var upper = t.FileName.ToUpperInvariant();
        var sameName = await db.DrawingRevisions.AsNoTracking().Where(v => v.FileName.ToUpper() == upper && db.Drawings.Any(d => d.Id == v.DrawingId && d.FolderId == folder.Id && !d.IsArchived))
            .Select(v => v.SizeBytes).ToListAsync(ct);
        if (sameName.Contains(t.Size))
        {
            await storage.DeleteAsync(t.StorageKey, ct);
            return new { Id = Guid.Empty, Skipped = true, Conflict = false, Linked = 0 };
        }
        if (sameName.Count > 0)
        {
            await storage.DeleteAsync(t.StorageKey, ct);
            throw new ConflictException($"{t.FileName} is already in this folder with different contents. Nothing was overwritten. Upload it as a new revision of that drawing.");
        }
        var title = DrawingService.Clean(Title(t.FileName), 300, "a title");
        var chosen = DrawingRevisionRules.Resolve(r.Revision, await RevisionsForNamesAsync((r.MatchNames ?? []).Append(title), ct), title, partMasterWins: true);
        rev = chosen.Revision;
        var d = new Drawing { Id = t.Drawing, FolderId = folder.Id, Title = title, CurrentRevision = rev, SiteId = folder.SiteId, DepartmentId = folder.DepartmentId };
        db.Drawings.Add(d);
        db.DrawingRevisions.Add(new DrawingRevision { Id = t.Revision, DrawingId = d.Id, Revision = rev, DrawingNumber = null, FileName = t.FileName, ContentType = DrawingService.TypeOf(t.Ext), StorageKey = t.StorageKey,
            SizeBytes = t.Size, Sha256 = "", Notes = chosen.FromPartMaster ? "Bulk import. Revision taken from the Part Master." : "Bulk import", Reason = chosen.FromPartMaster ? "Revision recorded in the Part Master" : "Bulk import", CreatedByName = me.UserName,
            Status = DrawingRevisionStatus.Current, ReleasedAt = DateTimeOffset.UtcNow, ReleasedByName = me.UserName });
        var linked = 0;
        if (r.LinkParts)
        {
            var hits = await PartsByNameAsync((r.MatchNames ?? []).Append(title), ct);
            foreach (var p in hits.Values.SelectMany(x => x).Select(x => x.Id).Distinct())
            {
                db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = d.Id, PartId = p, CreatedAt = DateTimeOffset.UtcNow });
                linked++;
            }
        }
        await db.SaveChangesAsync(ct);
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = DateTimeOffset.UtcNow, UserId = me.UserId, UserName = me.UserName, Action = "Uploaded revision", EntityType = nameof(Drawing), EntityId = d.Id, SiteId = d.SiteId,
            CorrelationId = me.CorrelationId,
            Description = AuditText.Fit($"Imported {d.AuditLabel} revision {rev} ({t.FileName}) into folder {folder.Name}. " +
                (chosen.FromPartMaster ? "The revision is the one recorded in the Part Master. It was not taken from the file name." : "The revision was entered for this import. It was not taken from the file name.") +
                " The file is stored on its own and does not replace another revision."),
        });
        await db.SaveChangesAsync(ct);
        return new { d.Id, Skipped = false, Conflict = false, Linked = linked };
    }

    /// <summary>One audit entry for the whole import, on the folder it went into.</summary>
    public async Task SummaryAsync(SummaryRequest r, CancellationToken ct)
    {
        var f = await RootAsync(r.FolderId, ct);
        static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.##} GB" : $"{b / (double)(1L << 20):0.#} MB";
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = DateTimeOffset.UtcNow, UserId = me.UserId, UserName = me.UserName, Action = "Imported drawings", EntityType = nameof(DrawingFolder), EntityId = f.Id ?? Guid.Empty, SiteId = f.SiteId,
            CorrelationId = me.CorrelationId,
            Description = AuditText.Fit($"{(r.Cancelled ? "Stopped an import" : "Imported")} into {(f.Id is null ? "the top level of Drawings (All folders)" : $"drawing folder {f.Name}")}: {r.Imported} drawings ({Size(r.Bytes)}), {r.Folders} new folders, " +
                $"{r.Skipped} already there, {r.Conflicts} left in place because a different file was already stored under that name, {r.Failed} failed, {r.Linked} part links"),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Runs the name matching again over every drawing the user can manage and adds the links that are missing (for example after parts were imported).
    /// Links are never removed. One audit entry per drawing that gained links, and one for the run.
    /// </summary>
    public async Task<object> RelinkAsync(CancellationToken ct)
    {
        var scope = await access.GetScopeAsync(UserId, Permissions.SequflohManageDrawings, ct);
        if (scope.IsEmpty) throw new ForbiddenException();
        var (checkedCount, added, per) = await matcher.LinkAsync(null, null, ct, scope.Allows);
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, site, title, n) in per)
            db.AuditEntries.Add(new AuditEntry
            {
                OccurredAt = now, UserId = me.UserId, UserName = me.UserName, Action = "Linked drawing to parts", EntityType = nameof(Drawing), EntityId = id, SiteId = site,
                CorrelationId = me.CorrelationId, Description = AuditText.Fit($"Relink drawings: drawing {title} linked to {n} more part{(n == 1 ? "" : "s")} with a matching part number, SENSEN # or drawing number field"),
            });
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = now, UserId = me.UserId, UserName = me.UserName, Action = "Relinked drawings", EntityType = nameof(DrawingFolder), EntityId = Guid.Empty,
            SiteId = await drawings.TopLevelSiteAsync(null, ct), CorrelationId = me.CorrelationId,
            Description = AuditText.Fit($"Relink drawings checked {checkedCount} drawings and added {added} part links to {per.Count} drawings"),
        });
        await db.SaveChangesAsync(ct);
        return new { Checked = checkedCount, Added = added, Drawings = per.Count };
    }
}
