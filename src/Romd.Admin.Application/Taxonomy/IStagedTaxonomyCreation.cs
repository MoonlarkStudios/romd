using Romd.Admin.Application.Taxonomy.ReadModels;
namespace Romd.Admin.Application.Taxonomy;

/// <summary>A new row's ID, readable only after owner flush inside the transaction that staged it.</summary>
public interface IStagedTaxonomyRow
{
    int ReadIdAfterFlush();
}

/// <summary>A new alias's immutable response, readable only after owner flush inside its original transaction.</summary>
public interface IStagedTaxonomyAlias
{
    TaxonomyAlias ReadAfterFlush();
}
