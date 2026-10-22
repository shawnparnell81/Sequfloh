using Microsoft.EntityFrameworkCore;

namespace Sequfloh.Api.Controllers.Sequfloh;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total);

internal static class Paging
{
    public static async Task<PagedResult<T>> PageAsync<T>(IQueryable<T> q, int page, int size, CancellationToken ct)
    {
        size = Math.Clamp(size, 1, 200);
        page = Math.Max(1, page);
        return new(await q.Skip((page - 1) * size).Take(size).ToListAsync(ct), await q.CountAsync(ct));
    }
}
