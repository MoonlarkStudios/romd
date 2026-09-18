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

    public async Task<Platform> AddAsync(Platform platform, CancellationToken cancellationToken = default)
    {
        var entity = PlatformEntity.FromDomain(platform);
        entity.IsEnabled = true;
        _context.Platforms.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);
        return entity.ToDomain();
    }

    public async Task AddRangeAsync(IReadOnlyList<Platform> platforms, CancellationToken cancellationToken = default)
    {
        if (platforms.Count == 0)
        {
            return;
        }

        var entities = platforms.Select(PlatformEntity.FromDomain).ToList();
        _context.Platforms.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
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
