using Romd.Admin.Application.Taxonomy.ReadModels;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Taxonomy;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

/// <summary>Private EF identities stay behind receipts bound to their original context transaction and tracking.</summary>
internal static class TaxonomyStagingReceipts
{
    internal static IStagedTaxonomyRow Row(RomdDbContext context, EntityBase entity) => new StagedRow(context, entity);
    internal static IStagedTaxonomyAlias Alias(RomdDbContext context, EntityBase entity, Func<TaxonomyAlias> read) =>
        new StagedAlias(new StagedRow(context, entity), read);

    internal static void RequireTransaction(RomdDbContext context)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Staging taxonomy creation requires the caller's transaction.");
    }

    private sealed class StagedRow : IStagedTaxonomyRow
    {
        private readonly RomdDbContext _context;
        private readonly EntityBase _entity;
        private readonly Guid _transaction;

        internal StagedRow(RomdDbContext context, EntityBase entity)
        {
            RequireTransaction(context);
            _context = context;
            _entity = entity;
            _transaction = context.Database.CurrentTransaction!.TransactionId;
        }

        public int ReadIdAfterFlush() => _context.Database.CurrentTransaction?.TransactionId == _transaction &&
            _context.Entry(_entity).State == EntityState.Unchanged && _entity.Id > 0
                ? _entity.Id
                : throw new InvalidOperationException("Read taxonomy creation only after owner flush in its original transaction.");
    }

    private sealed class StagedAlias(IStagedTaxonomyRow identity, Func<TaxonomyAlias> read) : IStagedTaxonomyAlias
    {
        public TaxonomyAlias ReadAfterFlush()
        {
            _ = identity.ReadIdAfterFlush();
            return read();
        }
    }
}
