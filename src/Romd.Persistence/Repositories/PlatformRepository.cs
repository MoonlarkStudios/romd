using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Source.Platform;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class PlatformRepository : IPlatformRepository
{
    private readonly RomdDbContext _context;

    public PlatformRepository(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<Platform?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Platforms
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        return entity?.ToDomain();
    }

    public async Task<Platform?> GetByShortNameAsync(string shortName, CancellationToken cancellationToken = default)
    {
        var entity = await _context.Platforms
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ShortName == shortName, cancellationToken);

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<Platform>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _context.Platforms
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await _context.Platforms
            .Where(p => p.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<bool> AnyAsync(CancellationToken cancellationToken = default) =>
        await _context.Platforms.AnyAsync(cancellationToken);
}
