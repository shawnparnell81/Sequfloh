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
    /// <param name="Primary">Whether the browser found this file to be its folder's primary drawing (named like the folder, or the only PDF or Word file in it).
    /// In a folder named for a part only the primary drawing is linked; the others are kept in the folder for reference. Left out, every file links as before.</param>
    public sealed record FinishRequest(string Token, string? UploadId, List<UploadedPart>? Parts, string? Revision, bool LinkParts, List<string>? MatchNames, bool? Primary = null);
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
    /// <summary>Folder names match ignoring capital letters and spaces at the start or end, so a second import reuses the folder instead of making another.</summary>
    static string NormName(string? name) => (name ?? "").Trim().ToUpperInvariant();

    /// <summary>The folders under a folder that are not archived, by path from it ("" is the folder itself).</summary>
    async Task<Dictionary<string, Guid?>> TreeAsync(Root root, CancellationToken ct)
    {
        var map = new Dictionary<string, Guid?> { [""] = root.Id };
        var level = new List<(Guid Id, string Path)>();
        if (root.Id is { } rid) level.Add((rid, ""));
        else
            foreach (var k in await db.DrawingFolders.AsNoTracking().Where(f => f.ParentId == null && f.SiteId == root.SiteId && !f.IsArchived).Select(f => new { f.Id, f.Name }).ToListAsync(ct))
            {
                var key = NormName(k.Name);
                if (key.Length > 0 && map.TryAdd(key, k.Id)) level.Add((k.Id, key));
            }
        while (level.Count > 0 && map.Count < 200_000)
        {
            var ids = level.Select(x => x.Id).ToList();
            var kids = await db.DrawingFolders.AsNoTracking().Where(f => f.ParentId != null && ids.Contains(f.ParentId.Value) && !f.IsArchived).Select(f => new { f.Id, f.ParentId, f.Name }).ToListAsync(ct);
            var paths = level.ToDictionary(x => x.Id, x => x.Path);
            level = [];
            foreach (var k in kids)
            {
                var name = NormName(k.Name);
                if (name.Length == 0) continue;
                var path = (paths[k.ParentId!.Value].Length == 0 ? "" : paths[k.ParentId.Value] + "/") + name;
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
        // only the folders and file names in this batch are looked up, so a drive of thousands of drawings does not scan every revision on every call
        var wanted = new Dictionary<Guid, HashSet<string>>();
        foreach (var f in files)
        {
            try
            {
                var s = Segments(f.Path);
                if (s.Length == 0 || !tree.TryGetValue(Key(s[..^1]), out var fid) || fid is not Guid fv) continue;
                if (!wanted.TryGetValue(fv, out var namesInFolder)) wanted[fv] = namesInFolder = new HashSet<string>(StringComparer.Ordinal);
                namesInFolder.Add(s[^1].ToUpperInvariant());
            }
            catch (AppException) { /* reported on the file row */ }
        }
        var have = new Dictionary<(Guid, string), List<long>>();
        foreach (var folderChunk in wanted.Keys.Chunk(200))
        {
            var folderIds = folderChunk.ToList();
            var nameList = folderIds.SelectMany(id => wanted[id]).Distinct(StringComparer.Ordinal).ToList();
            foreach (var nameChunk in nameList.Chunk(400))
            {
                var nameKeys = nameChunk.ToList();
                var rows = await db.DrawingRevisions.AsNoTracking()
                    .Join(db.Drawings.Where(d => folderIds.Contains(d.FolderId) && !d.IsArchived), v => v.DrawingId, d => d.Id, (v, d) => new { d.FolderId, v.FileName, v.SizeBytes })
                    .Where(x => nameKeys.Contains(x.FileName.ToUpper()))
                    .ToListAsync(ct);
                foreach (var x in rows)
                {
                    var k = (x.FolderId, x.FileName.ToUpperInvariant());
                    if (!have.TryGetValue(k, out var sizes)) have[k] = sizes = [];
                    sizes.Add(x.SizeBytes);
                }
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
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var s in paths)
            if (tree.TryGetValue(Key(s), out var id) && id is Guid g) result[string.Join("/", s)] = g;
        return result;
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
        var notes = chosen.Note ?? (chosen.FromPartMaster ? "Bulk import. Revision taken from the Part Master." : "Bulk import");
        var reason = chosen.Note ?? (chosen.FromPartMaster ? "Revision recorded in the Part Master" : "Bulk import");
        var d = new Drawing { Id = t.Drawing, FolderId = folder.Id, Title = title, CurrentRevision = rev, SiteId = folder.SiteId, DepartmentId = folder.DepartmentId };
        db.Drawings.Add(d);
        db.DrawingRevisions.Add(new DrawingRevision { Id = t.Revision, DrawingId = d.Id, Revision = rev, DrawingNumber = null, FileName = t.FileName, ContentType = DrawingService.TypeOf(t.Ext), StorageKey = t.StorageKey,
            SizeBytes = t.Size, Sha256 = "", Notes = notes, Reason = reason, CreatedByName = me.UserName,
            Status = DrawingRevisionStatus.Current, ReleasedAt = DateTimeOffset.UtcNow, ReleasedByName = me.UserName });
        var linked = 0;
        if (r.LinkParts)
        {
            IEnumerable<Guid> targets;
            if (r.Primary is bool primary)
            {
                var index = await matcher.IndexAsync(ct);
                targets = DrawingPartMatcher.Targets(index, index, title, null, folder.Name, primary).Select(x => x.Id);
            }
            else targets = (await PartsByNameAsync((r.MatchNames ?? []).Append(title), ct)).Values.SelectMany(x => x).Select(x => x.Id);
            foreach (var p in targets.Distinct())
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
                (chosen.Note ?? (chosen.FromPartMaster ? "The revision is the one recorded in the Part Master. It was not taken from the file name." : "The revision was entered for this import. It was not taken from the file name.")) +
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

    /// <summary>Folder paths ("Family/Sub/Name") for every folder, by id.</summary>
    async Task<Dictionary<Guid, string>> FolderPathsAsync(CancellationToken ct)
    {
        var rows = await db.DrawingFolders.AsNoTracking().Where(f => !f.IsArchived).Select(f => new { f.Id, f.ParentId, f.Name }).ToListAsync(ct);
        var byId = rows.ToDictionary(x => x.Id);
        var paths = new Dictionary<Guid, string>();
        string PathOf(Guid id, int depth)
        {
            if (paths.TryGetValue(id, out var known)) return known;
            var f = byId[id];
            var up = f.ParentId is Guid p && byId.ContainsKey(p) && depth < 50 ? PathOf(p, depth + 1) + "/" : "";
            return paths[id] = up + f.Name;
        }
        foreach (var f in rows) PathOf(f.Id, 0);
        return paths;
    }

    /// <summary>Every drawing the user can manage with its folder path, current file and linked parts, to compare against the source drive.</summary>
    public async Task<List<object>> InventoryAsync(CancellationToken ct)
    {
        var scope = await access.GetScopeAsync(UserId, Permissions.SequflohManageDrawings, ct);
        if (scope.IsEmpty) throw new ForbiddenException();
        var paths = await FolderPathsAsync(ct);
        var rows = (await db.Drawings.AsNoTracking().Where(d => !d.IsArchived)
            .Select(d => new { d.Id, d.FolderId, d.Title, d.CurrentRevision, d.SiteId, d.DepartmentId,
                File = db.DrawingRevisions.Where(v => v.DrawingId == d.Id && v.Revision == d.CurrentRevision).Select(v => new { v.FileName, v.SizeBytes }).FirstOrDefault() })
            .ToListAsync(ct)).Where(d => scope.Allows(d.SiteId, d.DepartmentId) && paths.ContainsKey(d.FolderId)).ToList();
        var links = (await db.DrawingPartLinks.AsNoTracking().Join(db.Parts, l => l.PartId, p => p.Id, (l, p) => new { l.DrawingId, p.PartNumber }).ToListAsync(ct))
            .GroupBy(x => x.DrawingId).ToDictionary(g => g.Key, g => g.Select(x => x.PartNumber).OrderBy(x => x, StringComparer.Ordinal).ToList());
        return rows.Select(d => (object)new { d.Id, Folder = paths[d.FolderId], d.Title, d.CurrentRevision, FileName = d.File?.FileName, Size = d.File?.SizeBytes,
            Parts = links.GetValueOrDefault(d.Id) ?? [] }).ToList();
    }

    public sealed record PrimaryLinksRequest(bool Apply = false);

    /// <summary>
    /// Brings part links in line with the primary-drawing rule (see DrawingPartMatcher.IsPrimary) for folders named for a part: the primary drawing gets
    /// any missing link, and links on the other bulk-imported files in that folder are removed so they stay as reference only. Drawings added by hand,
    /// file names, revisions, files and work orders already created are not touched. A folder with several files and none named for it is only reported.
    /// Without Apply nothing is saved and the changes are only listed.
    /// </summary>
    public async Task<object> PrimaryLinksAsync(PrimaryLinksRequest r, CancellationToken ct)
    {
        var scope = await access.GetScopeAsync(UserId, Permissions.SequflohManageDrawings, ct);
        if (scope.IsEmpty) throw new ForbiddenException();
        var index = await matcher.IndexAsync(ct);
        var paths = await FolderPathsAsync(ct);
        var all = (await db.Drawings.AsNoTracking().Where(d => !d.IsArchived)
            .Select(d => new { d.Id, d.FolderId, d.Title, d.DrawingNumber, d.SiteId, d.DepartmentId,
                Imported = db.DrawingRevisions.Where(v => v.DrawingId == d.Id).All(v =>
                    v.Reason == "Bulk import" || v.Reason == "Revision recorded in the Part Master"
                    || (v.Reason != null && v.Reason.Contains("The Part Master was not changed."))) })
            .ToListAsync(ct)).Where(d => paths.ContainsKey(d.FolderId)).ToList();
        var have = (await db.DrawingPartLinks.AsNoTracking().Select(l => new { l.DrawingId, l.PartId }).ToListAsync(ct)).ToLookup(l => l.DrawingId, l => l.PartId);
        var partNumbers = index.Values.SelectMany(x => x).DistinctBy(x => x.Id).ToDictionary(x => x.Id, x => x.PartNumber);
        var added = new List<(Guid Drawing, Guid Site, string Title, string Folder, List<Guid> Parts)>();
        var removed = new List<(Guid Drawing, Guid Site, string Title, string Folder, List<Guid> Parts)>();
        var choose = new List<object>();
        foreach (var g in all.GroupBy(d => d.FolderId))
        {
            var path = paths[g.Key];
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (!index.ContainsKey(DrawingPartMatcher.Normalize(name))) continue;
            var list = g.ToList();
            if (!list.All(d => scope.Allows(d.SiteId, d.DepartmentId))) continue;
            var primaries = list.Where(d => DrawingPartMatcher.IsPrimary(d.Title, name, list.Count)).ToList();
            if (primaries.Count == 0) { choose.Add(new { Folder = path, Files = list.Select(d => d.Title).OrderBy(x => x).ToList() }); continue; }
            foreach (var d in primaries)
            {
                var missing = DrawingPartMatcher.Targets(index, index, d.Title, d.DrawingNumber, name, true).Select(x => x.Id).Where(p => !have[d.Id].Contains(p)).Distinct().ToList();
                if (missing.Count > 0) added.Add((d.Id, d.SiteId, d.Title, path, missing));
            }
            foreach (var d in list.Except(primaries).Where(d => d.Imported))
            {
                var extra = have[d.Id].Distinct().ToList();
                if (extra.Count > 0) removed.Add((d.Id, d.SiteId, d.Title, path, extra));
            }
        }
        string Numbers(List<Guid> ids) => string.Join(", ", ids.Select(p => partNumbers.GetValueOrDefault(p) ?? p.ToString()).OrderBy(x => x, StringComparer.Ordinal));
        if (r.Apply && (added.Count > 0 || removed.Count > 0))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var a in added)
            {
                foreach (var p in a.Parts) db.DrawingPartLinks.Add(new DrawingPartLink { DrawingId = a.Drawing, PartId = p, CreatedAt = now });
                db.AuditEntries.Add(new AuditEntry { OccurredAt = now, UserId = me.UserId, UserName = me.UserName, Action = "Linked drawing to parts", EntityType = nameof(Drawing), EntityId = a.Drawing, SiteId = a.Site,
                    CorrelationId = me.CorrelationId, Description = AuditText.Fit($"Primary drawing check: drawing {a.Title} in folder {a.Folder} is the drawing named for its folder, so it was linked to part {Numbers(a.Parts)}") });
            }
            foreach (var x in removed)
            {
                var ids = x.Parts;
                await db.DrawingPartLinks.Where(l => l.DrawingId == x.Drawing && ids.Contains(l.PartId)).ExecuteDeleteAsync(ct);
                db.AuditEntries.Add(new AuditEntry { OccurredAt = now, UserId = me.UserId, UserName = me.UserName, Action = "Unlinked drawing from parts", EntityType = nameof(Drawing), EntityId = x.Drawing, SiteId = x.Site,
                    CorrelationId = me.CorrelationId, Description = AuditText.Fit($"Primary drawing check: drawing {x.Title} in folder {x.Folder} is a reference copy, not the drawing named for its folder, so its link to part {Numbers(x.Parts)} was removed. The file, its name and its revision were not changed and it stays in the folder") });
            }
            db.AuditEntries.Add(new AuditEntry { OccurredAt = now, UserId = me.UserId, UserName = me.UserName, Action = "Checked primary drawings", EntityType = nameof(DrawingFolder), EntityId = Guid.Empty,
                SiteId = await drawings.TopLevelSiteAsync(null, ct), CorrelationId = me.CorrelationId,
                Description = AuditText.Fit($"Primary drawing check linked {added.Count} primary drawings and removed the part links of {removed.Count} reference copies. {choose.Count} folders have several files and none named for the folder, and were left as they are") });
            await db.SaveChangesAsync(ct);
        }
        return new
        {
            Applied = r.Apply, Linked = added.Count, Unlinked = removed.Count, NeedsChoice = choose,
            LinkedDrawings = added.Select(a => new { a.Folder, a.Title, Parts = Numbers(a.Parts) }),
            UnlinkedDrawings = removed.Select(x => new { x.Folder, x.Title, Parts = Numbers(x.Parts) }),
        };
    }

}
