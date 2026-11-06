using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Diagnostics;
using Romd.Domain.Hashing;
using Romd.Persistence.Converters;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;

namespace Romd.Persistence;

public sealed class RomdDbContext : IdentityDbContext<RomdUser, RomdIdentityRole, Guid>
{
    // Set only after EfUnitOfWork locks a live execution row; never persisted.
    internal (Guid TransactionId, Guid JobId, Guid Token)? LockedJobExecution { get; set; }

    public RomdDbContext(DbContextOptions<RomdDbContext> options) : base(options)
    {
    }

    public IQueryable<Romd.Domain.Catalog.EnrichmentRequirementFacts> EnrichmentFacts(int[] titleIds, string policyVersion,
        bool cover, bool background, bool logo, string[] enabledProviders, string[] configuredProviders, string[] disabledProviders) =>
        FromExpression(() => EnrichmentFacts(titleIds, policyVersion, cover, background, logo, enabledProviders, configuredProviders, disabledProviders));

    public IQueryable<Romd.Persistence.Enrichment.EnrichmentBatchMember> EnrichmentBatchMembers(int[] values) =>
        FromExpression(() => EnrichmentBatchMembers(values));

    public DbSet<VfsConnectionEntity> VfsConnections => Set<VfsConnectionEntity>();
    public DbSet<RomVerificationEntity> Verifications => Set<RomVerificationEntity>();
    public DbSet<RomReconstructionMemberEntity> ReconstructionMembers => Set<RomReconstructionMemberEntity>();
    public DbSet<RomRepresentationOutputEntity> RepresentationOutputs => Set<RomRepresentationOutputEntity>();
    public DbSet<RomDiscMediaReferenceEntity> RomDiscMediaReferences => Set<RomDiscMediaReferenceEntity>();
    public DbSet<AccountSessionEntity> AccountSessions => Set<AccountSessionEntity>();
    public DbSet<CompanyEntity> Companies => Set<CompanyEntity>();
    public DbSet<SystemCompanyEntity> SystemCompanies => Set<SystemCompanyEntity>();
    public DbSet<RatingBoardEntity> RatingBoards => Set<RatingBoardEntity>();
    public DbSet<RatingEntity> Ratings => Set<RatingEntity>();
    public DbSet<ReferenceAssetEntity> ReferenceAssets => Set<ReferenceAssetEntity>();
    public DbSet<ReferenceAssetOwnerEntity> ReferenceAssetOwners => Set<ReferenceAssetOwnerEntity>();
    public DbSet<ReferenceCatalogStateEntity> ReferenceCatalogState => Set<ReferenceCatalogStateEntity>();

    public DbSet<CdArchiveOwnerEntity> CdArchiveOwners => Set<CdArchiveOwnerEntity>();
    public DbSet<OriginalDiscLayoutEntity> OriginalDiscLayouts => Set<OriginalDiscLayoutEntity>();
    public DbSet<OriginalDiscLayoutFileEntity> OriginalDiscLayoutFiles => Set<OriginalDiscLayoutFileEntity>();
    public DbSet<OriginalDiscLayoutAuditEntity> OriginalDiscLayoutAudit => Set<OriginalDiscLayoutAuditEntity>();
    public DbSet<MaterializedLibraryDiscSetEntity> MaterializedLibraryDiscSets => Set<MaterializedLibraryDiscSetEntity>();

    public DbSet<DiscSetEntity> DiscSets => Set<DiscSetEntity>();

    public DbSet<CatalogSourceEntity> CatalogSources => Set<CatalogSourceEntity>();
    public DbSet<SourceEntryEntity> SourceEntries => Set<SourceEntryEntity>();
    public DbSet<TitleSourceLinkEntity> TitleSourceLinks => Set<TitleSourceLinkEntity>();
    public DbSet<DatSubscriptionEntity> DatSubscriptions => Set<DatSubscriptionEntity>();
    public DbSet<DatSourceEntity> DatSources => Set<DatSourceEntity>();
    public DbSet<DatFileEntity> DatFiles => Set<DatFileEntity>();
    public DbSet<DatGameEntity> DatGames => Set<DatGameEntity>();
    public DbSet<DatRomEntity> DatRoms => Set<DatRomEntity>();
    public DbSet<DatDiskEntity> DatDisks => Set<DatDiskEntity>();
    public DbSet<CatalogReleaseEntity> CatalogReleases => Set<CatalogReleaseEntity>();
    public DbSet<CatalogReleaseFileEntity> CatalogReleaseFiles => Set<CatalogReleaseFileEntity>();
    public DbSet<CatalogReleaseSourceEntity> CatalogReleaseSources => Set<CatalogReleaseSourceEntity>();
    public DbSet<CatalogReleaseFileSourceEntity> CatalogReleaseFileSources => Set<CatalogReleaseFileSourceEntity>();
    public DbSet<CatalogReleaseRegionEntity> CatalogReleaseRegions => Set<CatalogReleaseRegionEntity>();
    public DbSet<CatalogReleaseLanguageEntity> CatalogReleaseLanguages => Set<CatalogReleaseLanguageEntity>();
    public DbSet<PlatformEntity> Platforms => Set<PlatformEntity>();
    public DbSet<PlatformAliasEntity> PlatformAliases => Set<PlatformAliasEntity>();
    public DbSet<TitleEntity> Titles => Set<TitleEntity>();
    public DbSet<TrackedTitleEntity> TrackedTitles => Set<TrackedTitleEntity>();
    public DbSet<ArtworkAssetEntity> ArtworkAssets => Set<ArtworkAssetEntity>();
    public DbSet<ArtworkVariantEntity> ArtworkVariants => Set<ArtworkVariantEntity>();
    public DbSet<ArtworkSelectionEntity> ArtworkSelections => Set<ArtworkSelectionEntity>();
    public DbSet<ArtworkPreferenceEntity> ArtworkPreferences => Set<ArtworkPreferenceEntity>();
    public DbSet<TitleMediaEntity> TitleMedia => Set<TitleMediaEntity>();
    public DbSet<TitleExternalIdEntity> TitleExternalIds => Set<TitleExternalIdEntity>();
    public DbSet<TitleMetadataLayerEntity> TitleMetadataLayers => Set<TitleMetadataLayerEntity>();
    public DbSet<TitleContentRatingEntity> TitleContentRatings => Set<TitleContentRatingEntity>();
    public DbSet<RomRepresentationEntity> RomRepresentations => Set<RomRepresentationEntity>();

    public DbSet<RomFileEntity> RomFiles => Set<RomFileEntity>();
    public DbSet<FileEntityPersistence> Files => Set<FileEntityPersistence>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<JobDispatchEntity> JobDispatches => Set<JobDispatchEntity>();
    public DbSet<MetadataRematerializationRequestEntity> MetadataRematerializationRequests => Set<MetadataRematerializationRequestEntity>();
    public DbSet<JobItemEntity> JobItems => Set<JobItemEntity>();
    public DbSet<AdminRealtimeOutboxEventEntity> AdminRealtimeOutboxEvents => Set<AdminRealtimeOutboxEventEntity>();
    public DbSet<AccountLinkEntity> AccountLinks => Set<AccountLinkEntity>();
    public DbSet<AdminAuditEventEntity> AdminAuditEvents => Set<AdminAuditEventEntity>();
    public DbSet<PlatformFieldDefaultEntity> PlatformFieldDefaults => Set<PlatformFieldDefaultEntity>();
    public DbSet<BiosEntity> Bios => Set<BiosEntity>();
    public DbSet<BiosGameMappingEntity> BiosGameMappings => Set<BiosGameMappingEntity>();
    public DbSet<RegionEntity> Regions => Set<RegionEntity>();
    public DbSet<RegionAliasEntity> RegionAliases => Set<RegionAliasEntity>();
    public DbSet<GameLanguageEntity> GameLanguages => Set<GameLanguageEntity>();
    public DbSet<GameLanguageAliasEntity> GameLanguageAliases => Set<GameLanguageAliasEntity>();
    public DbSet<DatGameRegionEntity> DatGameRegions => Set<DatGameRegionEntity>();
    public DbSet<DatGameLanguageEntity> DatGameLanguages => Set<DatGameLanguageEntity>();
    public DbSet<LibraryCollectionEntity> LibraryCollections => Set<LibraryCollectionEntity>();
    public DbSet<CollectionEntity> Collections => Set<CollectionEntity>();
    public DbSet<CollectionItemEntity> CollectionItems => Set<CollectionItemEntity>();
    public DbSet<LibraryEntity> Libraries => Set<LibraryEntity>();
    public DbSet<MaterializedLibraryTitleEntity> MaterializedLibraryTitles => Set<MaterializedLibraryTitleEntity>();
    public DbSet<MaterializedLibraryReleaseEntity> MaterializedLibraryReleases => Set<MaterializedLibraryReleaseEntity>();
    public DbSet<ConsumerUserSettingsEntity> ConsumerUserSettings => Set<ConsumerUserSettingsEntity>();
    public DbSet<PlaySessionEntity> PlaySessions => Set<PlaySessionEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Timestamps: DateTimeOffset → bigint (unix milliseconds)
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToMillisecondsConverter>();
        configurationBuilder.Properties<DateTimeOffset?>()
            .HaveConversion<NullableDateTimeOffsetToMillisecondsConverter>();

        // Hash types: domain structs → bytea
        ConfigureHashType<Sha256>(configurationBuilder);
        ConfigureHashType<Sha1>(configurationBuilder);
        ConfigureHashType<Md5>(configurationBuilder);
        ConfigureHashType<Crc32>(configurationBuilder);
    }

    private static void ConfigureHashType<T>(ModelConfigurationBuilder configurationBuilder)
        where T : struct, IHashValue<T>
    {
        configurationBuilder.Properties<T>()
            .HaveConversion<HashValueConverter<T>>()
            .HaveMaxLength(T.ByteLength);

        configurationBuilder.Properties<T?>()
            .HaveConversion<NullableHashValueConverter<T>>()
            .HaveMaxLength(T.ByteLength);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        CompanyEntity.Configure(modelBuilder.Entity<CompanyEntity>());
        SystemCompanyEntity.Configure(modelBuilder.Entity<SystemCompanyEntity>());
        RatingBoardEntity.Configure(modelBuilder.Entity<RatingBoardEntity>());
        RatingEntity.Configure(modelBuilder.Entity<RatingEntity>());
        ReferenceAssetEntity.Configure(modelBuilder.Entity<ReferenceAssetEntity>());
        ReferenceAssetOwnerEntity.Configure(modelBuilder.Entity<ReferenceAssetOwnerEntity>());
        ReferenceCatalogStateEntity.Configure(modelBuilder.Entity<ReferenceCatalogStateEntity>());
        base.OnModelCreating(modelBuilder);
        Romd.Persistence.ReferenceData.ReferenceSearch.Configure(modelBuilder, this.GetService<IRelationalTypeMappingSource>());
        // PostgreSQL 18's information-schema helper exposes unnest WITH ORDINALITY as named x/n columns.
        // This is a read-only result shape; no physical view is provisioned.
        modelBuilder.Entity<Romd.Persistence.Enrichment.EnrichmentBatchMember>().HasNoKey().ToView("EnrichmentBatchMembers");
        modelBuilder.Entity<Romd.Persistence.Enrichment.EnrichmentBatchMember>().Property(member => member.Value).HasColumnName("x");
        modelBuilder.Entity<Romd.Persistence.Enrichment.EnrichmentBatchMember>().Property(member => member.Ordinal).HasColumnName("n");
        modelBuilder.HasDbFunction(typeof(RomdDbContext).GetMethod(nameof(EnrichmentBatchMembers))!)
            .HasName("_pg_expandarray").HasSchema("information_schema");
        modelBuilder.Entity<Romd.Domain.Catalog.EnrichmentRequirementFacts>().HasNoKey().ToView("EnrichmentRequirementFacts");
        modelBuilder.HasDbFunction(typeof(RomdDbContext).GetMethod(nameof(EnrichmentFacts))!)
            .HasName("enrichment_requirement_facts_set").HasSchema(PostgreSqlConfiguration.SchemaName);
        modelBuilder.HasDefaultSchema(PostgreSqlConfiguration.SchemaName);

        MetadataProviderSettingsEntity.Configure(modelBuilder.Entity<MetadataProviderSettingsEntity>());
        CdArchiveOwnerEntity.Configure(modelBuilder.Entity<CdArchiveOwnerEntity>());
        OriginalDiscLayoutEntity.Configure(modelBuilder.Entity<OriginalDiscLayoutEntity>());
        OriginalDiscLayoutFileEntity.Configure(modelBuilder.Entity<OriginalDiscLayoutFileEntity>());
        OriginalDiscLayoutAuditEntity.Configure(modelBuilder.Entity<OriginalDiscLayoutAuditEntity>());
        DiscSetEntity.Configure(modelBuilder.Entity<DiscSetEntity>());
        CatalogSourceEntity.Configure(modelBuilder.Entity<CatalogSourceEntity>());
        SourceEntryEntity.Configure(modelBuilder.Entity<SourceEntryEntity>());
        TitleSourceLinkEntity.Configure(modelBuilder.Entity<TitleSourceLinkEntity>());
        DatSourceEntity.Configure(modelBuilder.Entity<DatSourceEntity>());
        DatSubscriptionEntity.Configure(modelBuilder.Entity<DatSubscriptionEntity>());
        DatFileEntity.Configure(modelBuilder.Entity<DatFileEntity>());
        DatGameEntity.Configure(modelBuilder.Entity<DatGameEntity>());
        DatRomEntity.Configure(modelBuilder.Entity<DatRomEntity>());
        DatDiskEntity.Configure(modelBuilder.Entity<DatDiskEntity>());
        CatalogReleaseEntity.Configure(modelBuilder.Entity<CatalogReleaseEntity>());
        CatalogReleaseFileEntity.Configure(modelBuilder.Entity<CatalogReleaseFileEntity>());
        CatalogReleaseSourceEntity.Configure(modelBuilder.Entity<CatalogReleaseSourceEntity>());
        CatalogReleaseFileSourceEntity.Configure(modelBuilder.Entity<CatalogReleaseFileSourceEntity>());
        CatalogReleaseRegionEntity.Configure(modelBuilder.Entity<CatalogReleaseRegionEntity>());
        CatalogReleaseLanguageEntity.Configure(modelBuilder.Entity<CatalogReleaseLanguageEntity>());
        PlatformEntity.Configure(modelBuilder.Entity<PlatformEntity>());
        PlatformAliasEntity.Configure(modelBuilder.Entity<PlatformAliasEntity>());
        TitleEntity.Configure(modelBuilder.Entity<TitleEntity>());
        TrackedTitleEntity.Configure(modelBuilder.Entity<TrackedTitleEntity>());
        ArtworkExclusionEntity.Configure(modelBuilder.Entity<ArtworkExclusionEntity>());
        ArtworkAssetEntity.Configure(modelBuilder.Entity<ArtworkAssetEntity>());
        ArtworkVariantEntity.Configure(modelBuilder.Entity<ArtworkVariantEntity>());
        ArtworkSelectionEntity.Configure(modelBuilder.Entity<ArtworkSelectionEntity>());
        ArtworkEnrichmentSettingsEntity.Configure(modelBuilder.Entity<ArtworkEnrichmentSettingsEntity>());
        ArtworkAcquisitionEntity.Configure(modelBuilder.Entity<ArtworkAcquisitionEntity>());
        EnrichmentOmissionEntity.Configure(modelBuilder.Entity<EnrichmentOmissionEntity>());
        Romd.Persistence.MetadataProviders.ProviderIdentityDatabaseFunctions.Configure(modelBuilder);
        ArtworkPreferenceEntity.Configure(modelBuilder.Entity<ArtworkPreferenceEntity>());
        TitleMediaEntity.Configure(modelBuilder.Entity<TitleMediaEntity>());
        TitleExternalIdEntity.Configure(modelBuilder.Entity<TitleExternalIdEntity>());
        TitleProviderMatchStateEntity.Configure(modelBuilder.Entity<TitleProviderMatchStateEntity>());
        TitleMetadataLayerEntity.Configure(modelBuilder.Entity<TitleMetadataLayerEntity>());
        TitleContentRatingEntity.Configure(modelBuilder.Entity<TitleContentRatingEntity>());
        RomFileEntity.Configure(modelBuilder.Entity<RomFileEntity>());
        RomRepresentationEntity.Configure(modelBuilder.Entity<RomRepresentationEntity>());
        RomRepresentationOutputEntity.Configure(modelBuilder.Entity<RomRepresentationOutputEntity>());
        RomReconstructionMemberEntity.Configure(modelBuilder.Entity<RomReconstructionMemberEntity>());
        RomVerificationEntity.Configure(modelBuilder.Entity<RomVerificationEntity>());
        RomDiscMediaReferenceEntity.Configure(modelBuilder.Entity<RomDiscMediaReferenceEntity>());
        FileEntityPersistence.Configure(modelBuilder.Entity<FileEntityPersistence>());
        AdminRealtimeOutboxEventEntity.Configure(modelBuilder.Entity<AdminRealtimeOutboxEventEntity>());
        JobItemEntity.Configure(modelBuilder.Entity<JobItemEntity>());
        AccountLinkEntity.Configure(modelBuilder.Entity<AccountLinkEntity>());
        AdminAuditEventEntity.Configure(modelBuilder.Entity<AdminAuditEventEntity>());
        PlatformFieldDefaultEntity.Configure(modelBuilder.Entity<PlatformFieldDefaultEntity>());
        BiosEntity.Configure(modelBuilder.Entity<BiosEntity>());
        BiosGameMappingEntity.Configure(modelBuilder.Entity<BiosGameMappingEntity>());
        RegionEntity.Configure(modelBuilder.Entity<RegionEntity>());
        RegionAliasEntity.Configure(modelBuilder.Entity<RegionAliasEntity>());
        GameLanguageEntity.Configure(modelBuilder.Entity<GameLanguageEntity>());
        GameLanguageAliasEntity.Configure(modelBuilder.Entity<GameLanguageAliasEntity>());
        DatGameRegionEntity.Configure(modelBuilder.Entity<DatGameRegionEntity>());
        DatGameLanguageEntity.Configure(modelBuilder.Entity<DatGameLanguageEntity>());
        LibraryCollectionEntity.Configure(modelBuilder.Entity<LibraryCollectionEntity>());
        CollectionEntity.Configure(modelBuilder.Entity<CollectionEntity>());
        CollectionItemEntity.Configure(modelBuilder.Entity<CollectionItemEntity>());
        LibraryEntity.Configure(modelBuilder.Entity<LibraryEntity>());
        MaterializedLibraryTitleEntity.Configure(modelBuilder.Entity<MaterializedLibraryTitleEntity>());
        MaterializedLibraryDiscSetEntity.Configure(modelBuilder.Entity<MaterializedLibraryDiscSetEntity>());
        MaterializedLibraryReleaseEntity.Configure(modelBuilder.Entity<MaterializedLibraryReleaseEntity>());
        ConsumerUserSettingsEntity.Configure(modelBuilder.Entity<ConsumerUserSettingsEntity>());
        PlaySessionEntity.Configure(modelBuilder.Entity<PlaySessionEntity>());

        ConfigureJobsHierarchy(modelBuilder);
        JobDispatchEntity.Configure(modelBuilder.Entity<JobDispatchEntity>());
        MetadataRematerializationRequestEntity.Configure(modelBuilder.Entity<MetadataRematerializationRequestEntity>());
        VfsConnectionEntity.Configure(modelBuilder.Entity<VfsConnectionEntity>());
        AccountSessionEntity.Configure(modelBuilder.Entity<AccountSessionEntity>());
        ConfigureIdentityExtensions(modelBuilder);
    }

    private static void ConfigureIdentityExtensions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RomdUser>(entity =>
        {
            entity.HasOne<LibraryEntity>()
                .WithMany()
                .HasForeignKey(u => u.LibraryId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(u => u.LibraryId);
        });
    }

    private static void ConfigureJobsHierarchy(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobEntity>(entity =>
        {
            entity.ToTable("Jobs");
            entity.HasKey(j => j.Id);

            entity.HasDiscriminator(j => j.JobType)
                .HasValue<UploadJobEntity>("upload")
                .HasValue<ReplaceDatJobEntity>("replace_dat")
                .HasValue<EnrichmentJobEntity>("enrichment")
                .HasValue<BulkEnrichmentJobEntity>("bulk_enrichment")
                .HasValue<ExportJobEntity>("export")
                .HasValue<MaterializationJobEntity>("materialization")
                .HasValue<ArtworkImportJobEntity>("artwork-import")
                .HasValue<ArtworkCompletionJobEntity>("artwork-completion")
                .HasValue<MetadataRematerializationJobEntity>(MetadataRematerializationJobEntity.Type)
                .HasValue<MediaReconciliationJobEntity>(MediaReconciliationJobEntity.Type);

            entity.Property(j => j.SourceFilename)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(j => j.Phase)
                .IsRequired()
                .HasMaxLength(50);

            entity.Property(j => j.HangfireJobId)
                .HasMaxLength(100);

            entity.Property(j => j.CurrentItem)
                .HasMaxLength(500);

            entity.Property(j => j.ErrorsJson)
                .IsRequired()
                .HasColumnType("TEXT");

            entity.Property(j => j.LastDeferralError).HasMaxLength(1000);

            entity.Property(j => j.LastAttemptError)
                .HasMaxLength(OperationalDiagnosticsPolicy.AttemptErrorLimit);

            entity.Property(j => j.JobType)
                .IsRequired()
                .HasMaxLength(50);

            entity.HasIndex(j => new
            {
                j.JobType,
                j.Phase,
                j.HangfireJobId,
                j.ExecutionLeaseExpiresAtUtc
            }).HasDatabaseName("IX_Jobs_ExecutionClaim");

            entity.HasIndex(j => j.Phase);
            entity.HasIndex(j => j.IsArchived);
            entity.HasIndex(j => j.CreatedAt);
            entity.HasIndex(j => new { j.IsArchived, j.CreatedAt });
            entity.HasIndex(j => new { j.JobType, j.Phase, j.UpdatedAt, j.Id })
                .HasDatabaseName("IX_Jobs_Diagnostics_ReplaceDat");
            entity.HasIndex(j => new { j.JobType, j.Phase, j.HangfireJobId, j.CreatedAt, j.Id })
                .HasDatabaseName("IX_Jobs_Diagnostics_BulkEnrichment");

            entity.HasOne<PlatformEntity>()
                .WithMany()
                .HasForeignKey(j => j.PlatformId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne<RomdUser>()
                .WithMany()
                .HasForeignKey(j => j.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(j => j.CreatedByUserId);
        });

        modelBuilder.Entity<UploadJobEntity>();
        MediaReconciliationDefinitionEntity.Configure(modelBuilder);
        MediaReconciliationPinEntity.Configure(modelBuilder);
        MediaReconciliationRequestEntity.Configure(modelBuilder);

        modelBuilder.Entity<ReplaceDatJobEntity>(entity =>
        {
            entity.Property(j => j.ExistingDatId).IsRequired();
        });

        modelBuilder.Entity<EnrichmentJobEntity>(entity =>
        {
            entity.Property(j => j.TitleId).IsRequired();

            entity.HasOne<TitleEntity>()
                .WithMany()
                .HasForeignKey(j => j.TitleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArtworkImportJobEntity>(entity =>
        {
            entity.Property(j => j.TitleId).HasColumnName("ArtworkTitleId");
            entity.Property(j => j.Role).HasColumnName("ArtworkRole").HasConversion<string>().HasMaxLength(20);
            entity.Property(j => j.SelectionRevision).HasColumnName("ArtworkSelectionRevision");
            entity.Property(j => j.ProviderId).HasColumnName("ArtworkProviderId").HasMaxLength(50);
            entity.Property(j => j.ProviderGameId).HasColumnName("ArtworkProviderGameId").HasMaxLength(100);
            entity.Property(j => j.ProviderAssetId).HasColumnName("ArtworkProviderAssetId").HasMaxLength(100);
            entity.Property(j => j.TrustedAssetUrl).HasColumnName("ArtworkTrustedAssetUrl").HasMaxLength(2048);
            entity.Property(j => j.Attribution).HasColumnName("ArtworkAttribution").HasMaxLength(500);
            entity.Property(j => j.FocalX).HasColumnName("ArtworkFocalX").HasDefaultValue(50).HasSentinel(50);
            entity.Property(j => j.FocalY).HasColumnName("ArtworkFocalY").HasDefaultValue(50).HasSentinel(50);
            entity.Property(j => j.RetainedAssetId).HasColumnName("ArtworkRetainedAssetId");
            entity.Property(j => j.WasSuperseded).HasColumnName("ArtworkWasSuperseded");
            // Title and outcome identities are historical snapshots, not foreign keys.
            // Cascading title deletion would invert the worker's job-then-title lock order.
            // Missing titles supersede pending imports; these IDs do not retain files.
            entity.ToTable(table => table.HasCheckConstraint("CK_Jobs_ArtworkImportIdentity",
                "\"JobType\" <> 'artwork-import' OR (\"ArtworkTitleId\" IS NOT NULL AND \"ArtworkTitleId\" > 0 " +
                "AND \"ArtworkRole\" IS NOT NULL AND \"ArtworkRole\" IN ('Poster', 'Hero', 'Logo', 'Backdrop') " +
                "AND \"ArtworkSelectionRevision\" IS NOT NULL AND \"ArtworkSelectionRevision\" > 0 " +
                "AND \"ArtworkProviderId\" IS NOT NULL AND length(btrim(\"ArtworkProviderId\")) > 0 " +
                "AND \"ArtworkProviderGameId\" IS NOT NULL AND length(btrim(\"ArtworkProviderGameId\")) > 0 " +
                "AND \"ArtworkProviderAssetId\" IS NOT NULL AND length(btrim(\"ArtworkProviderAssetId\")) > 0 " +
                "AND \"ArtworkTrustedAssetUrl\" IS NOT NULL AND length(btrim(\"ArtworkTrustedAssetUrl\")) > 0 " +
                "AND \"ArtworkWasSuperseded\" IS NOT NULL " +
                "AND \"Phase\" IN ('Pending', 'Importing', 'Completed', 'Failed', 'Cancelled'))"));
        });

        modelBuilder.Entity<BulkEnrichmentJobEntity>();
        modelBuilder.Entity<MetadataRematerializationJobEntity>().Property(x => x.TotalTitles).HasColumnName("RematerializationTotalTitles");
        modelBuilder.Entity<MetadataRematerializationJobEntity>().Property(x => x.ProcessedCount).HasColumnName("RematerializationProcessedCount");
        modelBuilder.Entity<MetadataRematerializationJobEntity>().Property(x => x.DeferredCount).HasColumnName("RematerializationDeferredCount");
        modelBuilder.Entity<MetadataRematerializationJobEntity>().Property(x => x.CursorTitleId).HasColumnName("RematerializationCursorTitleId");
        modelBuilder.Entity<ArtworkCompletionJobEntity>().Property(x => x.ScopeJson).HasColumnName("ArtworkCompletionScopeJson");
        modelBuilder.Entity<ArtworkCompletionJobEntity>().Property(x => x.Total).HasColumnName("ArtworkCompletionTotal");
        modelBuilder.Entity<ArtworkCompletionJobEntity>().Property(x => x.Processed).HasColumnName("ArtworkCompletionProcessed");
        ArtworkCompletionItemEntity.Configure(modelBuilder.Entity<ArtworkCompletionItemEntity>());
        ArtworkCompletionResultEntity.Configure(modelBuilder.Entity<ArtworkCompletionResultEntity>());

        modelBuilder.Entity<ExportJobEntity>(entity =>
        {
            entity.Property(j => j.TotalTitles).HasColumnName("ExportTotalTitles");
            entity.Property(j => j.ProcessedTitles).HasColumnName("ExportProcessedTitles");
            entity.Property(j => j.ExportPath).HasMaxLength(1000);
            entity.Property(j => j.LibraryId).HasColumnName("LibraryId");
            entity.Property(j => j.ExportScopeKind).HasMaxLength(20);
            entity.Property(j => j.ExportFormat).HasMaxLength(20);
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_Jobs_ExportFormat",
                "\"JobType\" <> 'export' OR \"ExportFormat\" IS NULL OR \"ExportFormat\" IN ('original', 'rvz', 'chd')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_Jobs_ExportScope",
                "\"JobType\" <> 'export' OR \"ExportScopeKind\" IS NULL OR " +
                "(\"ExportScopeKind\" = 'Library' AND \"LibraryId\" IS NOT NULL AND \"LibraryId\" > 0 AND " +
                "\"AuthorizedMaterializationGeneration\" IS NOT NULL AND \"AuthorizedMaterializationGeneration\" >= 0) OR " +
                "(\"ExportScopeKind\" = 'AllCatalog' AND \"LibraryId\" IS NULL AND \"AuthorizedMaterializationGeneration\" IS NULL)"));
        });

        modelBuilder.Entity<MaterializationJobEntity>(entity =>
        {
            entity.Property(j => j.LibraryId).HasColumnName("LibraryId");
            entity.HasIndex(j => j.LibraryId)
                .IsUnique()
                .HasFilter(
                    "\"JobType\" = 'materialization' AND \"Phase\" NOT IN ('Completed', 'CompletedWithErrors', 'Failed', 'Cancelled', 'Deferred')")
                .HasDatabaseName("IX_MaterializationJobs_Active_LibraryId");
        });
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StageJobDispatches();
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.Any(entry => entry.Entity is TitleEntity))
        {
            throw new PersistenceConflictException(ex);
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StageJobDispatches();
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex) when (ex.Entries.Any(entry => entry.Entity is TitleEntity))
        {
            throw new PersistenceConflictException(ex);
        }
    }

    private void StageJobDispatches()
    {
        var staged = ChangeTracker.Entries<JobDispatchEntity>()
            .Select(entry => entry.Entity.JobId).ToHashSet();
        foreach (var entry in ChangeTracker.Entries<JobEntity>().ToArray())
        {
            if (entry.State != EntityState.Added || entry.Entity.Phase != "Pending"
                || !staged.Add(entry.Entity.Id))
                continue;

            var job = entry.Entity;
            JobDispatches.Add(new JobDispatchEntity
            {
                JobId = job.Id,
                JobType = job.JobType,
                CreatedAtUtc = job.CreatedAt,
                AvailableAtUtc = job.CreatedAt
            });
        }
    }
}
