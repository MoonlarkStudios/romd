using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Configuration;

public static class EntityConfigurationExtensions
{
    public static EntityTypeBuilder<TEntity> ConfigureEntity<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        string tableName) where TEntity : EntityBase
    {
        builder.ToTable(tableName);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        return builder;
    }

    public static EntityTypeBuilder<TEntity> ConfigureGuidEntity<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        string tableName) where TEntity : GuidEntityBase
    {
        builder.ToTable(tableName);
        builder.HasKey(e => e.Id);
        return builder;
    }
}
